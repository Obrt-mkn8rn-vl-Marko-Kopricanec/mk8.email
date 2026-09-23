using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Application.Services;

internal sealed class SieveFilterService(
    EmailDbContext database,
    SieveScriptContentService contentService,
    ILogger<SieveFilterService> logger) : ISieveFilterService
{
    public async Task<SieveDeliveryPlan> EvaluateAsync(
        string envelopeSender,
        string recipient,
        string rawMessage,
        string defaultFolder,
        CancellationToken cancellationToken = default)
    {
        var route = await ResolveRouteAsync(recipient, cancellationToken);
        if (route is null)
            return DefaultPlan(defaultFolder);
        var script = await database.SieveScripts
            .AsNoTracking()
            .Where(item => item.UserId == route.UserId && item.IsActive)
            .SingleOrDefaultAsync(cancellationToken);
        if (script is null)
            return DefaultPlan(defaultFolder);

        var mailboxes = (await database.Folders
                .AsNoTracking()
                .Where(folder => folder.InboxId == route.InboxId)
                .Select(folder => folder.Name)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var compilation = SieveScript.Compile(
            await contentService.ReadAsync(script, cancellationToken));
        if (!compilation.Succeeded)
        {
            logger.LogError(
                "Active Sieve script {ScriptId} is invalid at line {Line}, column {Column}: {Message}",
                script.Id,
                compilation.Diagnostics[0].Line,
                compilation.Diagnostics[0].Column,
                compilation.Diagnostics[0].Message);
            return DefaultPlan(defaultFolder);
        }

        try
        {
            var result = SieveScript.Evaluate(
                compilation.Program!,
                new SieveMessageContext(
                    envelopeSender,
                    recipient,
                    rawMessage,
                    defaultFolder,
                    mailboxes));
            if (result.Deliveries.Any(delivery =>
                    !MailboxName.IsValid(delivery.Folder)
                    || !delivery.Create
                        && !mailboxes.Contains(MailboxName.Normalize(delivery.Folder))))
            {
                logger.LogWarning(
                    "Sieve script {ScriptId} attempted delivery to an unavailable mailbox; applying implicit keep",
                    script.Id);
                return DefaultPlan(defaultFolder);
            }
            return new SieveDeliveryPlan(
                true,
                result.Deliveries.Select(delivery => new SieveDeliveryInstruction(
                    MailboxName.Normalize(delivery.Folder),
                    delivery.Flags,
                    delivery.Create)).ToArray(),
                result.Redirects,
                result.RejectReason,
                result.Discarded);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Sieve evaluation failed for script {ScriptId}", script.Id);
            return DefaultPlan(defaultFolder);
        }
    }

    private async Task<SieveRoute?> ResolveRouteAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var separator = address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Length - 1)
            return null;
        var localPart = address[..separator].ToLowerInvariant();
        var domain = address[(separator + 1)..].ToLowerInvariant();
        var route = await database.Inboxes
            .AsNoTracking()
            .Where(inbox => (inbox.Name == localPart || inbox.Name == "*")
                && inbox.Address.Domain == domain
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive
                && (inbox.Name != "*" || inbox.AliasForInboxId != null))
            .OrderBy(inbox => inbox.Name == localPart ? 0 : 1)
            .Select(inbox => new { inbox.Id, inbox.AliasForInboxId })
            .FirstOrDefaultAsync(cancellationToken);
        if (route is null)
            return null;
        var targetId = route.AliasForInboxId ?? route.Id;
        return await database.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.Id == targetId
                && inbox.AliasForInboxId == null
                && inbox.Owner.IsActive
                && inbox.Address.IsActive
                && inbox.Address.Company.IsActive)
            .Select(inbox => new SieveRoute(inbox.Id, inbox.OwnerId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static SieveDeliveryPlan DefaultPlan(string folder) => new(
        false,
        [new SieveDeliveryInstruction(folder, [], false)],
        [],
        null,
        false);

    private sealed record SieveRoute(Guid InboxId, Guid UserId);
}
