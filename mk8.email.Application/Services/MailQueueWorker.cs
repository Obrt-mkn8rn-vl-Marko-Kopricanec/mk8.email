using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using Npgsql;

namespace mk8.email.Application.Services;

public sealed class MailQueueWorker(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig environment,
    TimeProvider timeProvider,
    ILogger<MailQueueWorker> logger) : BackgroundService
{
    private const int MaximumSieveRedirectDepth = 10;
    private const int MaximumSieveRedirectRecipients = 100;
    private static readonly TimeSpan DeliveryDelayNotificationThreshold = TimeSpan.FromHours(4);
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);
        await MigrateQueueContentAsync(stoppingToken).ConfigureAwait(false);

        if (IsPostgreSql())
        {
            await RunNotificationLoopAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        await RunPollingLoopAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                if (!processed)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(environment.Queue.PollIntervalMilliseconds),
                        timeProvider,
                        stoppingToken).ConfigureAwait(false);
                }

                await CleanupCompletedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ApplicationServiceLog.QueueWorkerFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunNotificationLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var listener = new NpgsqlConnection(environment.BuildConnectionString());
                await using (listener.ConfigureAwait(false))
                {
                    await listener.OpenAsync(stoppingToken).ConfigureAwait(false);
                    {
                        var command = listener.CreateCommand();
                        await using var commandLifetime = command.ConfigureAwait(false);
                        command.CommandText = "LISTEN mk8_mail_queue_ready";
                        await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                    }

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var processed = await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                        await CleanupCompletedAsync(stoppingToken).ConfigureAwait(false);
                        if (!processed)
                        {
                            var delay = await GetNextWakeDelayAsync(stoppingToken).ConfigureAwait(false);
                            await listener.WaitAsync(
                                Math.Max(1, checked((int)delay.TotalMilliseconds)),
                                stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ApplicationServiceLog.QueueNotificationListenerFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<TimeSpan> GetNextWakeDelayAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var nextPending = await database.MailQueueMessages
            .Where(message => message.State == MailQueueStates.Pending)
            .Select(message => (DateTime?)message.NextAttemptAt)
            .MinAsync(cancellationToken).ConfigureAwait(false);
        var nextLease = await database.MailQueueMessages
            .Where(message => message.State == MailQueueStates.Processing)
            .Select(message => message.LeaseExpiresAt)
            .MinAsync(cancellationToken).ConfigureAwait(false);
        var next = new[] { nextPending, nextLease }
            .Where(candidate => candidate is not null)
            .Min();
        var fallback = TimeSpan.FromSeconds(
            Math.Clamp(environment.Messaging.NotificationFallbackSeconds, 1, 300));
        if (next is null)
            return fallback;

        var untilDue = next.Value - timeProvider.GetUtcNow().UtcDateTime;
        if (untilDue <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(10);
        return untilDue < fallback ? untilDue : fallback;
    }

    private bool IsPostgreSql()
    {
        using var scope = scopeFactory.CreateScope();
        return IsPostgreSql(scope.ServiceProvider.GetRequiredService<EmailDbContext>());
    }

    private static bool IsPostgreSql(EmailDbContext database) =>
        string.Equals(
            database.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var leaseToken = Guid.CreateVersion7();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var messageId = await ClaimNextAsync(database, leaseToken, now, cancellationToken).ConfigureAwait(false);
        if (messageId is null)
            return false;

        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The renewal task is cancelled and directly awaited in this method's finally block.
#pragma warning disable CA2025
        var renewal = IsPostgreSql(database)
            ? RenewLeaseAsync(messageId.Value, leaseToken, processingCancellation)
            : Task.CompletedTask;
#pragma warning restore CA2025
        try
        {
            try
            {
                await ProcessClaimedAsync(
                    scope.ServiceProvider,
                    database,
                    messageId.Value,
                    leaseToken,
                    now,
                    processingCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ApplicationServiceLog.QueueProcessingFailed(logger, exception, messageId.Value);
                await ReleaseAfterFailureAsync(
                    database,
                    messageId.Value,
                    leaseToken,
                    exception,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await processingCancellation.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
        }

        return true;
    }

    private async Task RenewLeaseAsync(
        Guid messageId,
        Guid leaseToken,
        CancellationTokenSource processingCancellation)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, environment.Queue.LeaseSeconds / 3));
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(processingCancellation.Token).ConfigureAwait(false))
            {
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    var renewed = await database.MailQueueMessages
                        .Where(message => message.Id == messageId
                            && message.State == MailQueueStates.Processing
                            && message.LeaseToken == leaseToken
                            && message.LeaseExpiresAt > now)
                        .ExecuteUpdateAsync(
                            updates => updates.SetProperty(
                                message => message.LeaseExpiresAt,
                                now.AddSeconds(environment.Queue.LeaseSeconds)),
                            processingCancellation.Token).ConfigureAwait(false);
                    if (renewed == 1)
                        continue;

                    var state = await database.MailQueueMessages
                        .AsNoTracking()
                        .Where(message => message.Id == messageId)
                        .Select(message => message.State)
                        .SingleOrDefaultAsync(processingCancellation.Token).ConfigureAwait(false);
                    if (string.Equals(state, MailQueueStates.Processing, StringComparison.Ordinal))
                    {
                        ApplicationServiceLog.QueueProcessingLeaseLost(logger, messageId);
                        await processingCancellation.CancelAsync().ConfigureAwait(false);
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplicationServiceLog.QueueLeaseRenewalFailed(logger, exception, messageId);
            await processingCancellation.CancelAsync().ConfigureAwait(false);
        }
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
.ConfigureAwait(false) ?? throw new InvalidOperationException("The claimed queue message is unavailable.");

        var scanner = services.GetRequiredService<IMailScanner>();
        var delivery = services.GetRequiredService<IEmailService>();
        var content = services.GetRequiredService<MailQueueContentService>();
        var vacationResponder = services.GetService<IVacationResponder>();
        var sieveFilter = services.GetService<ISieveFilterService>();
        var relay = services.GetRequiredService<IOutboundMailRelay>();
        var rawMessage = await content.ReadAsync(message, cancellationToken).ConfigureAwait(false);

        if (string.Equals(message.ScanState, MailQueueScanStates.Pending, StringComparison.Ordinal))
        {
            var scan = await scanner.ScanAsync(
                new MailScanRequest(
                    message.Id,
                    message.EnvelopeSender,
                    message.Recipients.Select(item => item.Recipient).ToList(),
                    rawMessage,
                    message.ClientIp,
                    message.Helo,
                    message.AuthenticatedUser),
                cancellationToken).ConfigureAwait(false);

            if (scan.IsTemporaryFailure)
            {
                ScheduleMessageRetry(message, "The mail scanner requested a temporary retry.", now);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
                || (string.Equals(message.Direction, MailQueueDirections.Submission, StringComparison.Ordinal) && IsSpamAction(scan.Action)))
            {
                await QuarantineAsync(message, delivery, now, cancellationToken).ConfigureAwait(false);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                ApplicationServiceLog.QueueMessageQuarantined(logger, message.Id);
                return;
            }
        }

        var deliveryMessage = (message.AddedHeaders ?? string.Empty) + rawMessage;

        if (string.Equals(message.Direction, MailQueueDirections.Submission, StringComparison.Ordinal) && !message.SentCopyCreated)
        {
            if (!await delivery.SaveSentCopyAsync(
                    message.EnvelopeSender,
                    deliveryMessage,
                    message.Id,
                    cancellationToken).ConfigureAwait(false))
            {
                ScheduleMessageRetry(message, "The sent copy could not be stored.", now);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            message.SentCopyCreated = true;
        }

        foreach (var recipient in message.Recipients
                     .Where(item => string.Equals(
                         item.State, MailQueueRecipientStates.Pending, StringComparison.Ordinal)
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
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var recipient in message.Recipients.OrderBy(item => item.Id))
        {
            if (string.Equals(recipient.State, MailQueueRecipientStates.Delivered, StringComparison.Ordinal))
            {
                if (recipient.DsnForwarded
                    || !ShouldNotifySuccess(recipient)
                    || string.IsNullOrEmpty(message.EnvelopeSender))
                {
                    recipient.SuccessNoticeCreated = true;
                }
                else if (!recipient.SuccessNoticeCreated)
                {
                    var action = recipient.IsLocal
                        ? WasExpanded(message, recipient)
                            ? DeliveryStatusAction.Expanded
                            : DeliveryStatusAction.Delivered
                        : DeliveryStatusAction.Relayed;
                    if (await SendDeliveryStatusNotificationAsync(
                            message,
                            recipient,
                            action,
                            rawMessage,
                            delivery,
                            relay,
                            now,
                            cancellationToken).ConfigureAwait(false))
                    {
                        recipient.SuccessNoticeCreated = true;
                    }
                    else if (IsExpired(message.AttemptCount, message.ReceivedAt, now))
                    {
                        recipient.SuccessNoticeCreated = true;
                        ApplicationServiceLog.SuccessDsnAbandoned(logger, recipient.Id);
                    }
                    else
                    {
                        ScheduleNoticeRetry(recipient, now);
                    }
                }
            }
            else if (string.Equals(recipient.State, MailQueueRecipientStates.PermanentFailure, StringComparison.Ordinal))
            {
                if (!ShouldNotifyFailure(recipient)
                    || string.IsNullOrEmpty(message.EnvelopeSender))
                {
                    recipient.FailureNoticeCreated = true;
                }
                else if (!recipient.FailureNoticeCreated)
                {
                    if (await SendDeliveryStatusNotificationAsync(
                            message,
                            recipient,
                            DeliveryStatusAction.Failed,
                            rawMessage,
                            delivery,
                            relay,
                            now,
                            cancellationToken).ConfigureAwait(false))
                    {
                        recipient.FailureNoticeCreated = true;
                    }
                    else if (IsExpired(message.AttemptCount, message.ReceivedAt, now))
                    {
                        recipient.FailureNoticeCreated = true;
                        ApplicationServiceLog.FailureDsnAbandoned(logger, recipient.Id);
                    }
                    else
                    {
                        ScheduleNoticeRetry(recipient, now);
                    }
                }
            }
            else if (string.Equals(
                    recipient.State, MailQueueRecipientStates.Pending, StringComparison.Ordinal)
                && !recipient.DelayNoticeCreated
                && ShouldNotifyDelay(recipient)
                && now - message.ReceivedAt >= DeliveryDelayNotificationThreshold
                && !string.IsNullOrEmpty(message.EnvelopeSender)
                && await SendDeliveryStatusNotificationAsync(
                    message,
                    recipient,
                    DeliveryStatusAction.Delayed,
                    rawMessage,
                    delivery,
                    relay,
                    now,
                    cancellationToken).ConfigureAwait(false))
            {
                recipient.DelayNoticeCreated = true;
            }
        }

        FinalizeMessageState(message, now);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
                        cancellationToken).ConfigureAwait(false);

                if (plan.RejectReason is not null)
                {
                    if (!await CreateSieveRejectionAsync(
                            message,
                            recipient,
                            plan.RejectReason,
                            delivery,
                            relay,
                            cancellationToken).ConfigureAwait(false))
                    {
                        ScheduleRecipientRetry(
                            message,
                            recipient,
                            "The Sieve rejection notice could not be delivered.",
                            now);
                        return;
                    }
                    recipient.SuccessNoticeCreated = true;
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
                    cancellationToken).ConfigureAwait(false);
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
                        instruction.Create).ConfigureAwait(false);
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
                            cancellationToken).ConfigureAwait(false))
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
                new OutboundMailOptions(
                    message.RequiresSmtpUtf8,
                    new MailDsnEnvelope(
                        message.DsnReturnContent,
                        message.DsnEnvelopeId),
                    new MailDsnRecipient(
                        recipient.DsnNotify,
                        recipient.DsnOriginalRecipient)),
                cancellationToken).ConfigureAwait(false);
            if (result.Status == OutboundDeliveryStatus.Delivered)
            {
                recipient.DsnForwarded = result.DsnParametersForwarded;
                recipient.LastEnhancedStatusCode = result.EnhancedStatusCode;
                recipient.LastRemoteMta = result.RemoteMta;
                MarkDelivered(recipient, now);
                return;
            }

            if (result.Status == OutboundDeliveryStatus.PermanentFailure)
            {
                MarkPermanentFailure(
                    recipient,
                    result.Detail,
                    now,
                    result.EnhancedStatusCode,
                    result.RemoteMta);
                return;
            }

            ScheduleRecipientRetry(
                message,
                recipient,
                result.Detail,
                now,
                result.EnhancedStatusCode,
                result.RemoteMta);
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

    private static async Task<int> AddSieveRedirectsAsync(
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
            await database.MailQueueRecipients.AddAsync(new MailQueueRecipientDB
            {
                Id = redirectId,
                MessageId = message.Id,
                Message = message,
                Recipient = redirect,
                IsLocal = await delivery.CanReceiveAsync(redirect, cancellationToken).ConfigureAwait(false),
                State = MailQueueRecipientStates.Pending,
                NextAttemptAt = now,
                RedirectDepth = source.RedirectDepth + 1,
                RedirectHistory = redirectHistory,
                DsnNotify = RemoveSuccessNotification(source.DsnNotify),
                DsnOriginalRecipient = source.DsnOriginalRecipient,
            }, cancellationToken).ConfigureAwait(false);
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
        var noticeText =
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
        var rawNotice = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(noticeText));

        if (await delivery.CanReceiveAsync(message.EnvelopeSender, cancellationToken).ConfigureAwait(false))
        {
            return await delivery.DeliverAsync(
                sender,
                message.EnvelopeSender,
                rawNotice,
                DefaultFolders.Inbox,
                DeriveQueueDeliveryId(recipient.Id, "sieve-reject"),
                cancellationToken).ConfigureAwait(false);
        }

        var result = await relay.RelayAsync(
            string.Empty,
            message.EnvelopeSender,
            rawNotice,
            new OutboundMailOptions(
                message.RequiresSmtpUtf8
                || message.EnvelopeSender.Any(character => !char.IsAscii(character)),
                RecipientDsn: new MailDsnRecipient("NEVER")),
            cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> SendDeliveryStatusNotificationAsync(
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient,
        DeliveryStatusAction action,
        string rawMessage,
        IEmailService delivery,
        IOutboundMailRelay relay,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.EnvelopeSender))
            return true;

        var report = DeliveryStatusNotificationBuilder.Build(
            message,
            recipient,
            action,
            rawMessage,
            environment.Smtp.Hostname,
            new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)),
            recipient.LastError,
            recipient.LastEnhancedStatusCode,
            recipient.LastRemoteMta);

        if (await delivery.CanReceiveAsync(message.EnvelopeSender, cancellationToken).ConfigureAwait(false))
        {
            return await delivery.DeliverAsync(
                string.Empty,
                message.EnvelopeSender,
                report.RawMessage,
                DefaultFolders.Inbox,
                DeriveQueueDeliveryId(recipient.Id, $"dsn-{action}"),
                cancellationToken).ConfigureAwait(false);
        }

        var result = await relay.RelayAsync(
            string.Empty,
            message.EnvelopeSender,
            report.RawMessage,
            new OutboundMailOptions(
                report.RequiresSmtpUtf8,
                RecipientDsn: new MailDsnRecipient("NEVER")),
            cancellationToken).ConfigureAwait(false);
        return result.Status is OutboundDeliveryStatus.Delivered
            or OutboundDeliveryStatus.PermanentFailure;
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

        if (!string.Equals(message.Direction, MailQueueDirections.Submission, StringComparison.Ordinal)
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
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid?> ClaimNextAsync(
        EmailDbContext database,
        Guid leaseToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        // A null transaction is intentional for non-relational test providers.
#pragma warning disable CA2007, MA0004
        await using (transaction)
        {
#pragma warning restore CA2007, MA0004
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
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
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
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            if (message is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            message.State = MailQueueStates.Processing;
            message.LeaseToken = leaseToken;
            message.LeaseExpiresAt = now.AddSeconds(environment.Queue.LeaseSeconds);
            message.AttemptCount++;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            database.ChangeTracker.Clear();
            return message.Id;
        }
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
            cancellationToken).ConfigureAwait(false);
        if (message is null)
            return;

        ScheduleMessageRetry(message, GetSafeError(exception), now);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        DateTime now,
        string? enhancedStatusCode = null,
        string? remoteMta = null)
    {
        recipient.LastEnhancedStatusCode = NormalizeOptionalMetadata(enhancedStatusCode, 16);
        recipient.LastRemoteMta = NormalizeOptionalMetadata(remoteMta, 255);
        if (IsExpired(recipient.AttemptCount, message.ReceivedAt, now))
        {
            MarkPermanentFailure(
                recipient,
                detail,
                now,
                recipient.LastEnhancedStatusCode,
                recipient.LastRemoteMta);
            return;
        }

        recipient.NextAttemptAt = now + GetRetryDelay(recipient.AttemptCount);
        recipient.LastError = SanitizeError(detail);
    }

    private static void FinalizeMessageState(MailQueueMessageDB message, DateTime now)
    {
        var pendingNotices = message.Recipients.Where(item =>
                (string.Equals(item.State, MailQueueRecipientStates.PermanentFailure, StringComparison.Ordinal)
                    && !item.FailureNoticeCreated)
                || (string.Equals(item.State, MailQueueRecipientStates.Delivered, StringComparison.Ordinal)
                    && !item.SuccessNoticeCreated))
            .ToList();
        var pending = message.Recipients
            .Where(item => string.Equals(item.State, MailQueueRecipientStates.Pending, StringComparison.Ordinal))
            .ToList();
        if (pending.Count > 0 || pendingNotices.Count > 0)
        {
            message.State = MailQueueStates.Pending;
            message.NextAttemptAt = pending
                .Concat(pendingNotices)
                .Min(item => item.NextAttemptAt);
            message.LeaseToken = null;
            message.LeaseExpiresAt = null;
            return;
        }

        if (string.Equals(message.Direction, MailQueueDirections.Inbound, StringComparison.Ordinal)
            && message.Recipients.Any(item => string.Equals(
                item.State, MailQueueRecipientStates.PermanentFailure, StringComparison.Ordinal)))
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
        recipient.DelayNoticeCreated = true;
    }

    private static void MarkPermanentFailure(
        MailQueueRecipientDB recipient,
        string detail,
        DateTime now,
        string? enhancedStatusCode = null,
        string? remoteMta = null)
    {
        recipient.State = MailQueueRecipientStates.PermanentFailure;
        recipient.CompletedAt = now;
        recipient.LastError = SanitizeError(detail);
        recipient.LastEnhancedStatusCode = NormalizeOptionalMetadata(enhancedStatusCode, 16);
        recipient.LastRemoteMta = NormalizeOptionalMetadata(remoteMta, 255);
        recipient.SuccessNoticeCreated = true;
        recipient.DelayNoticeCreated = true;
    }

    private static bool ShouldNotifySuccess(MailQueueRecipientDB recipient) =>
        !SmtpDsn.SuppressesAll(recipient.DsnNotify)
        && SmtpDsn.Requests(recipient.DsnNotify, "SUCCESS");

    private static bool ShouldNotifyFailure(MailQueueRecipientDB recipient) =>
        !SmtpDsn.SuppressesAll(recipient.DsnNotify)
        && (recipient.DsnNotify is null
            || SmtpDsn.Requests(recipient.DsnNotify, "FAILURE"));

    private static bool ShouldNotifyDelay(MailQueueRecipientDB recipient) =>
        !SmtpDsn.SuppressesAll(recipient.DsnNotify)
        && SmtpDsn.Requests(recipient.DsnNotify, "DELAY");

    private static bool WasExpanded(
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient) =>
        message.Recipients.Any(candidate =>
            candidate.Id != recipient.Id
            && candidate.RedirectDepth == recipient.RedirectDepth + 1
            && candidate.RedirectHistory
                .Take(Math.Max(0, candidate.RedirectHistory.Length - 1))
                .Contains(recipient.Recipient, StringComparer.OrdinalIgnoreCase));

    private static string? RemoveSuccessNotification(string? notify)
    {
        if (notify is null || SmtpDsn.SuppressesAll(notify))
            return notify;

        var remaining = notify.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(value => !value.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return remaining.Length == 0 ? "NEVER" : string.Join(',', remaining);
    }

    private static void ScheduleNoticeRetry(MailQueueRecipientDB recipient, DateTime now)
    {
        recipient.NextAttemptAt = now + GetRetryDelay(Math.Max(1, recipient.AttemptCount));
    }

    private static string? NormalizeOptionalMetadata(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var sanitized = SanitizeError(value);
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
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
            .EnsureAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateQueueContentAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<MailQueueLargeObjectMigrationService>()
            .MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CleanupCompletedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextCleanup)
            return;

        var cutoff = now.UtcDateTime.AddDays(-environment.Queue.CompletedRetentionDays);
        while (await CleanupCompletedBatchAsync(cutoff, cancellationToken).ConfigureAwait(false) == 1000)
        {
        }
        _nextCleanup = now.AddHours(1);
    }

    private async Task<int> CleanupCompletedBatchAsync(
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var content = scope.ServiceProvider.GetRequiredService<MailQueueContentService>();
        var effects = scope.ServiceProvider.GetRequiredService<LargeObjectTransactionEffects>();
        var marker = effects.Mark();
        IDbContextTransaction? transaction = null;
        var commitAttempted = false;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var expired = await database.MailQueueMessages
                .Where(message => message.State == MailQueueStates.Completed
                    && message.CompletedAt < cutoff)
                .OrderBy(message => message.CompletedAt)
                .Take(1000)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (expired.Count == 0)
            {
                if (transaction is not null)
                {
                    commitAttempted = true;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                effects.Discard(marker);
                return 0;
            }

            foreach (var message in expired)
            {
                var reference = content.TryGetReference(message);
                if (reference is not null)
                    effects.DeleteOnCommit(reference);
            }
            database.MailQueueMessages.RemoveRange(expired);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                commitAttempted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await effects.CommitAsync(marker).ConfigureAwait(false);
            ApplicationServiceLog.CompletedQueueRecordsRemoved(logger, expired.Count);
            return expired.Count;
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    ApplicationServiceLog.CompletedQueueCleanupRollbackFailed(logger, rollbackException);
                }
            }
            if (commitAttempted)
                effects.Discard(marker);
            else
                await effects.RollbackAsync(marker).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
