using System.Data;
using Microsoft.EntityFrameworkCore;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public sealed class MailQueueMaintenanceService(
    EmailDbContext database,
    MailQueueContentService content,
    ILargeObjectStore objects)
{
    public async Task<bool> PurgeQuarantinedSmokeMessageAsync(
        string marker,
        CancellationToken cancellationToken = default)
    {
        if (marker.Length != 32 || marker.Any(character => character is not
                (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The smoke marker must be 32 lowercase hexadecimal characters.",
                nameof(marker));
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var matches = await database.MailQueueMessages
            .Where(message => message.DsnEnvelopeId == marker
                && message.EnvelopeSender == "probe@debian.org"
                && message.Direction == MailQueueDirections.Inbound)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (matches.Count != 1 || matches[0].State != MailQueueStates.Quarantined)
            return false;

        var message = matches[0];
        var reference = content.TryGetReference(message);
        if (message.RawMessage is not null
            || reference is null
            || reference.ObjectName != MailQueueContentService.BuildObjectName(message.Id))
        {
            throw new InvalidOperationException("The quarantined smoke message has no valid Blob reference.");
        }

        var raw = await content.ReadAsync(message, cancellationToken);
        var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0 || !raw[..headerEnd].Split("\r\n")
                .Contains($"X-Mk8-Test: {marker}", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The quarantined message does not match its smoke marker.");
        }

        database.MailQueueMessages.Remove(message);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!await objects.DeleteIfMatchAsync(reference, cancellationToken))
        {
            throw new InvalidOperationException(
                "The quarantined smoke message was removed, but its Blob was not deleted.");
        }

        return true;
    }
}
