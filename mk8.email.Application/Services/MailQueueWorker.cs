using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MailQueueWorker(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig environment,
    TimeProvider timeProvider,
    ILogger<MailQueueWorker> logger) : BackgroundService
{
    private const int MaximumSieveRedirectDepth = 10;
    private const int MaximumSieveRedirectRecipients = 100;
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureSchemaAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(environment.Queue.PollIntervalMilliseconds),
                        timeProvider,
                        stoppingToken);
                }

                await CleanupCompletedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The mail queue worker failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
            }
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var leaseToken = Guid.CreateVersion7();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var messageId = await ClaimNextAsync(database, leaseToken, now, cancellationToken);
        if (messageId is null)
            return false;

        try
        {
            await ProcessClaimedAsync(
                scope.ServiceProvider,
                database,
                messageId.Value,
                leaseToken,
                now,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Queue processing failed for {QueueId}", messageId);
            await ReleaseAfterFailureAsync(
                database,
                messageId.Value,
                leaseToken,
                exception,
                now,
                cancellationToken);
        }

        return true;
    }

    private async Task ProcessClaimedAsync(
        IServiceProvider services,
        EmailDbContext database,
        Guid messageId,
        Guid leaseToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var message = await database.MailQueueMessages
            .Include(item => item.Recipients)
            .SingleOrDefaultAsync(
                item => item.Id == messageId
                    && item.State == MailQueueStates.Processing
                    && item.LeaseToken == leaseToken,
                cancellationToken)
            ?? throw new InvalidOperationException("The claimed queue message is unavailable.");

        var scanner = services.GetRequiredService<IMailScanner>();
        var delivery = services.GetRequiredService<IEmailService>();
        var vacationResponder = services.GetService<IVacationResponder>();
        var sieveFilter = services.GetService<ISieveFilterService>();
        var relay = services.GetRequiredService<IOutboundMailRelay>();

        if (message.ScanState == MailQueueScanStates.Pending)
        {
            var scan = await scanner.ScanAsync(
                new MailScanRequest(
                    message.Id,
                    message.EnvelopeSender,
                    message.Recipients.Select(item => item.Recipient).ToList(),
                    message.RawMessage,
                    message.ClientIp,
                    message.Helo,
                    message.AuthenticatedUser),
                cancellationToken);

            if (scan.IsTemporaryFailure)
            {
                ScheduleMessageRetry(message, "The mail scanner requested a temporary retry.", now);
                await database.SaveChangesAsync(cancellationToken);
                return;
            }

            message.ScanState = MailQueueScanStates.Complete;
            message.ScanAction = scan.Action;
            message.ScanScore = scan.Score;
            message.AddedHeaders = scan.AddedHeaders;
            message.TargetFolder = IsSpamAction(scan.Action)
                ? DefaultFolders.Spam
                : DefaultFolders.Inbox;

            if (scan.IsMalware
                || (message.Direction == MailQueueDirections.Submission && IsSpamAction(scan.Action)))
            {
                await QuarantineAsync(message, delivery, now, cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Queue message {QueueId} was quarantined", message.Id);
                return;
            }
        }

        var deliveryMessage = (message.AddedHeaders ?? string.Empty) + message.RawMessage;

        if (message.Direction == MailQueueDirections.Submission && !message.SentCopyCreated)
        {
            if (!await delivery.SaveSentCopyAsync(
                    message.EnvelopeSender,
                    deliveryMessage,
                    message.Id,
                    cancellationToken))
            {
                ScheduleMessageRetry(message, "The sent copy could not be stored.", now);
                await database.SaveChangesAsync(cancellationToken);
                return;
            }

            message.SentCopyCreated = true;
        }

        foreach (var recipient in message.Recipients
                     .Where(item => item.State == MailQueueRecipientStates.Pending
                         && item.NextAttemptAt <= now)
                     .OrderBy(item => item.Id)
                     .ToList())
        {
            await DeliverRecipientAsync(
                database,
                message,
                recipient,
                deliveryMessage,
                delivery,
                vacationResponder,
                sieveFilter,
                relay,
                now,
                cancellationToken);
        }

        foreach (var recipient in message.Recipients.Where(item =>
                     item.State == MailQueueRecipientStates.PermanentFailure
                     && !item.FailureNoticeCreated))
        {
            if (!await CreateFailureNoticeAsync(
                    message,
                    recipient,
                    delivery,
                    cancellationToken))
            {
                MarkDead(message, "A delivery failure notice could not be stored.", now);
                await database.SaveChangesAsync(cancellationToken);
                return;
            }

            recipient.FailureNoticeCreated = true;
        }

        FinalizeMessageState(message, now);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task DeliverRecipientAsync(
        EmailDbContext database,
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient,
        string rawMessage,
        IEmailService delivery,
        IVacationResponder? vacationResponder,
        ISieveFilterService? sieveFilter,
        IOutboundMailRelay relay,
        DateTime now,
        CancellationToken cancellationToken)
    {
        recipient.AttemptCount++;
        recipient.LastAttemptAt = now;

        try
        {
            if (recipient.IsLocal)
            {
                var defaultFolder = message.TargetFolder ?? DefaultFolders.Inbox;
                var plan = sieveFilter is null
                    ? new SieveDeliveryPlan(
                        false,
                        [new SieveDeliveryInstruction(defaultFolder, [], false)],
                        [],
                        null,
                        false)
                    : await sieveFilter.EvaluateAsync(
                        message.EnvelopeSender,
                        recipient.Recipient,
                        rawMessage,
                        defaultFolder,
                        cancellationToken);

                if (plan.RejectReason is not null)
                {
                    if (!await CreateSieveRejectionAsync(
                            message,
                            recipient,
                            plan.RejectReason,
                            delivery,
                            relay,
                            cancellationToken))
                    {
                        ScheduleRecipientRetry(
                            message,
                            recipient,
                            "The Sieve rejection notice could not be delivered.",
                            now);
                        return;
                    }
                    MarkDelivered(recipient, now);
                    return;
                }

                var redirectsAdded = await AddSieveRedirectsAsync(
                    database,
                    message,
                    recipient,
                    plan.Redirects,
                    delivery,
                    now,
                    cancellationToken);
                var deliveries = plan.Deliveries;
                if (deliveries.Count == 0
                    && plan.Redirects.Count > 0
                    && redirectsAdded == 0
                    && !plan.Discarded)
                {
                    deliveries = [new SieveDeliveryInstruction(defaultFolder, [], false)];
                }

                for (var index = 0; index < deliveries.Count; index++)
                {
                    var instruction = deliveries[index];
                    var deliveryId = plan.ScriptApplied
                        ? DeriveQueueDeliveryId(recipient.Id, $"sieve-delivery-{index}")
                        : recipient.Id;
                    var delivered = await delivery.DeliverAsync(
                        message.EnvelopeSender,
                        recipient.Recipient,
                        rawMessage,
                        instruction.Folder,
                        deliveryId,
                        cancellationToken,
                        instruction.Flags,
                        instruction.Create);
                    if (!delivered)
                    {
                        ScheduleRecipientRetry(
                            message,
                            recipient,
                            "The local mailbox is unavailable, missing, or over quota.",
                            now);
                        return;
                    }

                    if (vacationResponder is not null
                        && !await vacationResponder.QueueResponseAsync(
                            message.EnvelopeSender,
                            recipient.Recipient,
                            rawMessage,
                            instruction.Folder,
                            deliveryId,
                            cancellationToken))
                    {
                        ScheduleRecipientRetry(
                            message,
                            recipient,
                            "The vacation response could not be queued.",
                            now);
                        return;
                    }
                }

                MarkDelivered(recipient, now);
                return;
            }

            var result = await relay.RelayAsync(
                message.EnvelopeSender,
                recipient.Recipient,
                rawMessage,
                cancellationToken);
            if (result.Status == OutboundDeliveryStatus.Delivered)
            {
                MarkDelivered(recipient, now);
                return;
            }

            if (result.Status == OutboundDeliveryStatus.PermanentFailure)
            {
                MarkPermanentFailure(recipient, result.Detail, now);
                return;
            }

            ScheduleRecipientRetry(message, recipient, result.Detail, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ScheduleRecipientRetry(message, recipient, GetSafeError(exception), now);
        }
    }

    private async Task<int> AddSieveRedirectsAsync(
        EmailDbContext database,
        MailQueueMessageDB message,
        MailQueueRecipientDB source,
        IReadOnlyList<string> redirects,
        IEmailService delivery,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (redirects.Count == 0 || source.RedirectDepth >= MaximumSieveRedirectDepth)
            return 0;

        var history = new HashSet<string>(
            source.RedirectHistory.Length > 0
                ? source.RedirectHistory
                : [source.Recipient],
            StringComparer.OrdinalIgnoreCase);
        history.Add(source.Recipient);
        var added = 0;
        for (var index = 0; index < redirects.Count; index++)
        {
            var redirect = redirects[index];
            if (history.Contains(redirect)
                || message.Recipients.Count >= MaximumSieveRedirectRecipients)
            {
                continue;
            }

            var redirectId = DeriveQueueDeliveryId(source.Id, $"sieve-redirect-{index}-{redirect}");
            if (message.Recipients.Any(item => item.Id == redirectId))
            {
                added++;
                continue;
            }

            var redirectHistory = source.RedirectHistory
                .Append(source.Recipient)
                .Append(redirect)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            database.MailQueueRecipients.Add(new MailQueueRecipientDB
            {
                Id = redirectId,
                MessageId = message.Id,
                Message = message,
                Recipient = redirect,
                IsLocal = await delivery.CanReceiveAsync(redirect, cancellationToken),
                State = MailQueueRecipientStates.Pending,
                NextAttemptAt = now,
                RedirectDepth = source.RedirectDepth + 1,
                RedirectHistory = redirectHistory,
            });
            added++;
        }
        return added;
    }

    private async Task<bool> CreateSieveRejectionAsync(
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient,
        string reason,
        IEmailService delivery,
        IOutboundMailRelay relay,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.EnvelopeSender))
            return true;

        var host = environment.Smtp.Hostname;
        var safeReason = SanitizeError(reason);
        var sender = $"mailer-daemon@{host}";
        var rawNotice =
            $"From: Mail Delivery System <{sender}>\r\n" +
            $"To: {message.EnvelopeSender}\r\n" +
            "Subject: Message rejected by recipient policy\r\n" +
            $"Date: {timeProvider.GetUtcNow():r}\r\n" +
            $"Message-ID: <sieve-reject-{recipient.Id:N}@{host}>\r\n" +
            "Auto-Submitted: auto-replied\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n" +
            $"Delivery to {recipient.Recipient} was rejected by the recipient's mail policy.\r\n\r\n" +
            $"{safeReason}\r\n";

        if (await delivery.CanReceiveAsync(message.EnvelopeSender, cancellationToken))
        {
            return await delivery.DeliverAsync(
                sender,
                message.EnvelopeSender,
                rawNotice,
                DefaultFolders.Inbox,
                DeriveQueueDeliveryId(recipient.Id, "sieve-reject"),
                cancellationToken);
        }

        var result = await relay.RelayAsync(
            string.Empty,
            message.EnvelopeSender,
            rawNotice,
            cancellationToken);
        return result.Status is OutboundDeliveryStatus.Delivered
            or OutboundDeliveryStatus.PermanentFailure;
    }

    private static Guid DeriveQueueDeliveryId(Guid sourceId, string purpose)
    {
        var source = sourceId.ToByteArray();
        var label = Encoding.UTF8.GetBytes(purpose);
        var input = new byte[source.Length + label.Length];
        source.CopyTo(input, 0);
        label.CopyTo(input, source.Length);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task<bool> CreateFailureNoticeAsync(
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient,
        IEmailService delivery,
        CancellationToken cancellationToken)
    {
        if (message.Direction != MailQueueDirections.Submission
            || string.IsNullOrEmpty(message.AuthenticatedUser))
        {
            recipient.FailureNoticeCreated = true;
            return true;
        }

        var host = environment.Smtp.Hostname;
        var failureDetail = SanitizeError(recipient.LastError ?? "Delivery failed.");
        var rawNotice =
            $"From: Mail Delivery System <mailer-daemon@{host}>\r\n" +
            $"To: {message.AuthenticatedUser}\r\n" +
            "Subject: Mail delivery failed\r\n" +
            $"Date: {timeProvider.GetUtcNow():r}\r\n" +
            $"Message-ID: <failure-{recipient.Id:N}@{host}>\r\n" +
            "Auto-Submitted: auto-replied\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n\r\n" +
            $"Delivery to {recipient.Recipient} failed.\r\n\r\n{failureDetail}\r\n";

        return await delivery.DeliverAsync(
            $"mailer-daemon@{host}",
            message.AuthenticatedUser,
            rawNotice,
            DefaultFolders.Inbox,
            recipient.Id,
            cancellationToken);
    }

    private async Task QuarantineAsync(
        MailQueueMessageDB message,
        IEmailService delivery,
        DateTime now,
        CancellationToken cancellationToken)
    {
        message.State = MailQueueStates.Quarantined;
        message.CompletedAt = now;
        message.LastError = "The message matched the malware or outbound abuse policy.";
        message.LeaseToken = null;
        message.LeaseExpiresAt = null;
        foreach (var recipient in message.Recipients)
        {
            recipient.State = MailQueueRecipientStates.Quarantined;
            recipient.CompletedAt = now;
            recipient.LastError = message.LastError;
            recipient.FailureNoticeCreated = true;
        }

        if (message.Direction != MailQueueDirections.Submission
            || string.IsNullOrEmpty(message.AuthenticatedUser))
        {
            return;
        }

        var host = environment.Smtp.Hostname;
        var rawNotice =
            $"From: Mail Security <mailer-daemon@{host}>\r\n" +
            $"To: {message.AuthenticatedUser}\r\n" +
            "Subject: Message quarantined\r\n" +
            $"Date: {timeProvider.GetUtcNow():r}\r\n" +
            $"Message-ID: <quarantine-{message.Id:N}@{host}>\r\n" +
            "Auto-Submitted: auto-replied\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            "The server quarantined your message. An administrator must review the queue record.\r\n";

        _ = await delivery.DeliverAsync(
            $"mailer-daemon@{host}",
            message.AuthenticatedUser,
            rawNotice,
            DefaultFolders.Inbox,
            message.Id,
            cancellationToken);
    }

    private async Task<Guid?> ClaimNextAsync(
        EmailDbContext database,
        Guid leaseToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        MailQueueMessageDB? message;
        if (string.Equals(
                database.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            var candidates = await database.MailQueueMessages
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM mail_queue_messages
                    WHERE next_attempt_at <= {now}
                      AND (
                        state = {MailQueueStates.Pending}
                        OR (state = {MailQueueStates.Processing} AND lease_expires_at <= {now})
                      )
                    ORDER BY next_attempt_at, received_at, id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                    """)
                .ToListAsync(cancellationToken);
            message = candidates.SingleOrDefault();
        }
        else
        {
            message = await database.MailQueueMessages
                .Where(item => item.NextAttemptAt <= now
                    && (item.State == MailQueueStates.Pending
                        || (item.State == MailQueueStates.Processing
                            && item.LeaseExpiresAt <= now)))
                .OrderBy(item => item.NextAttemptAt)
                .ThenBy(item => item.ReceivedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (message is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        message.State = MailQueueStates.Processing;
        message.LeaseToken = leaseToken;
        message.LeaseExpiresAt = now.AddSeconds(environment.Queue.LeaseSeconds);
        message.AttemptCount++;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();
        return message.Id;
    }

    private async Task ReleaseAfterFailureAsync(
        EmailDbContext database,
        Guid messageId,
        Guid leaseToken,
        Exception exception,
        DateTime now,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var message = await database.MailQueueMessages.SingleOrDefaultAsync(
            item => item.Id == messageId && item.LeaseToken == leaseToken,
            cancellationToken);
        if (message is null)
            return;

        ScheduleMessageRetry(message, GetSafeError(exception), now);
        await database.SaveChangesAsync(cancellationToken);
    }

    private void ScheduleMessageRetry(MailQueueMessageDB message, string detail, DateTime now)
    {
        if (IsExpired(message.AttemptCount, message.ReceivedAt, now))
        {
            MarkDead(message, detail, now);
            return;
        }

        message.State = MailQueueStates.Pending;
        message.NextAttemptAt = now + GetRetryDelay(message.AttemptCount);
        message.LastError = SanitizeError(detail);
        message.LeaseToken = null;
        message.LeaseExpiresAt = null;
    }

    private void ScheduleRecipientRetry(
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient,
        string detail,
        DateTime now)
    {
        if (IsExpired(recipient.AttemptCount, message.ReceivedAt, now))
        {
            MarkPermanentFailure(recipient, detail, now);
            return;
        }

        recipient.NextAttemptAt = now + GetRetryDelay(recipient.AttemptCount);
        recipient.LastError = SanitizeError(detail);
    }

    private void FinalizeMessageState(MailQueueMessageDB message, DateTime now)
    {
        var hasPendingNotice = message.Recipients.Any(item =>
            item.State == MailQueueRecipientStates.PermanentFailure
            && !item.FailureNoticeCreated);
        var pending = message.Recipients
            .Where(item => item.State == MailQueueRecipientStates.Pending)
            .ToList();
        if (pending.Count > 0 || hasPendingNotice)
        {
            message.State = MailQueueStates.Pending;
            message.NextAttemptAt = pending.Count > 0
                ? pending.Min(item => item.NextAttemptAt)
                : now;
            message.LeaseToken = null;
            message.LeaseExpiresAt = null;
            return;
        }

        if (message.Direction == MailQueueDirections.Inbound
            && message.Recipients.Any(item =>
                item.State == MailQueueRecipientStates.PermanentFailure))
        {
            MarkDead(message, "A local inbound recipient could not accept the message.", now);
            return;
        }

        message.State = MailQueueStates.Completed;
        message.CompletedAt = now;
        message.LastError = null;
        message.LeaseToken = null;
        message.LeaseExpiresAt = null;
    }

    private static void MarkDelivered(MailQueueRecipientDB recipient, DateTime now)
    {
        recipient.State = MailQueueRecipientStates.Delivered;
        recipient.CompletedAt = now;
        recipient.LastError = null;
        recipient.FailureNoticeCreated = true;
    }

    private static void MarkPermanentFailure(
        MailQueueRecipientDB recipient,
        string detail,
        DateTime now)
    {
        recipient.State = MailQueueRecipientStates.PermanentFailure;
        recipient.CompletedAt = now;
        recipient.LastError = SanitizeError(detail);
    }

    private static void MarkDead(MailQueueMessageDB message, string detail, DateTime now)
    {
        message.State = MailQueueStates.Dead;
        message.CompletedAt = now;
        message.LastError = SanitizeError(detail);
        message.LeaseToken = null;
        message.LeaseExpiresAt = null;
    }

    private bool IsExpired(int attempts, DateTime receivedAt, DateTime now) =>
        attempts >= environment.Queue.MaxAttempts
        || now - receivedAt >= TimeSpan.FromHours(environment.Queue.MaxAgeHours);

    private static bool IsSpamAction(string action) =>
        action is "add header" or "rewrite subject" or "reject" or "discard";

    private static TimeSpan GetRetryDelay(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 8);
        return TimeSpan.FromMinutes(Math.Min(360, 1 << exponent));
    }

    private static string GetSafeError(Exception exception) =>
        SanitizeError(exception.GetBaseException().Message);

    private static string SanitizeError(string value)
    {
        var normalized = string.Join(
            ' ',
            value.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<MailRuntimeSchemaService>()
            .EnsureAsync(cancellationToken);
    }

    private async Task CleanupCompletedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextCleanup)
            return;

        _nextCleanup = now.AddHours(1);
        var cutoff = now.UtcDateTime.AddDays(-environment.Queue.CompletedRetentionDays);
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var expired = await database.MailQueueMessages
            .Where(message => message.State == MailQueueStates.Completed
                && message.CompletedAt < cutoff)
            .OrderBy(message => message.CompletedAt)
            .Take(1000)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
            return;

        database.MailQueueMessages.RemoveRange(expired);
        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Removed {Count} completed queue records", expired.Count);
    }
}
