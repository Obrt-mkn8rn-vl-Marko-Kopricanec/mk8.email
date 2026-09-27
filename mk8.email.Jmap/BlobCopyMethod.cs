using mk8.email.Contracts.Messaging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class BlobCopyMethod(
    JmapAccountService accounts,
    JmapBlobService blobs,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.CopyBinaryObjects;
    public MailFeature Feature => MailFeature.Basic;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "fromAccountId", "accountId", "blobIds")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "fromAccountId", out var fromAccountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || string.Equals(fromAccountId, accountId, StringComparison.Ordinal)
            || !JmapEmailArguments.TryGetIds(arguments, "blobIds", false, out var blobIds)
            || blobIds is null)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (blobIds.Count > environment.Jmap.MaxObjectsInSet)
            return JmapMethodResponse.Error("requestTooLarge");
        var sourceAccount = await accounts.GetAccountAsync(context.User, fromAccountId, cancellationToken).ConfigureAwait(false);
        if (sourceAccount is null)
            return JmapMethodResponse.Error("fromAccountNotFound");
        var targetAccount = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (targetAccount is null)
            return JmapMethodResponse.Error("accountNotFound");

        var copied = new JsonObject();
        var notCopied = new JsonObject();
        foreach (var blobId in blobIds.Distinct(StringComparer.Ordinal))
        {
            var source = await blobs.GetAsync(sourceAccount.InboxId, blobId, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                notCopied[blobId] = JmapMethodHelpers.SetError("notFound");
                continue;
            }
            if (source.Content.LongLength > environment.Jmap.MaxUploadSizeBytes)
            {
                notCopied[blobId] = JmapMethodHelpers.SetError("tooLarge");
                continue;
            }
            var stored = await blobs.StoreAsync(
                targetAccount.InboxId,
                source.Content,
                source.ContentType,
                source.Name,
                cancellationToken).ConfigureAwait(false);
            copied[blobId] = stored.BlobId;
        }
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["fromAccountId"] = fromAccountId,
            ["accountId"] = accountId,
            ["copied"] = copied.Count == 0 ? null : copied,
            ["notCopied"] = notCopied.Count == 0 ? null : notCopied,
        });
    }
}
