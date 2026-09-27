using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailParseMethod(
    JmapAccountService accounts,
    JmapBlobService blobs,
    EnvironmentConfig environment) : IJmapMethod
{
    public string Name => "Email/parse";
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "blobIds",
                "properties",
                "bodyProperties",
                "fetchTextBodyValues",
                "fetchHTMLBodyValues",
                "fetchAllBodyValues",
                "maxBodyValueBytes")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapEmailArguments.TryGetIds(arguments, "blobIds", false, out var blobIds)
            || blobIds is null
            || !JmapEmailArguments.TryGetProjectionOptions(
                arguments,
                JmapEmailCodec.ParseDefaultProperties,
                allowNullProperties: false,
                out var options))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (blobIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");

        var parsed = new JsonObject();
        var notParsable = new JsonArray();
        var notFound = new JsonArray();
        foreach (var blobId in blobIds.Distinct(StringComparer.Ordinal))
        {
            var blob = await blobs.GetAsync(account.InboxId, blobId, cancellationToken).ConfigureAwait(false);
            if (blob is null)
            {
                notFound.Add(blobId);
                continue;
            }
            try
            {
                using var message = JmapEmailCodec.Parse(blob.Content);
                parsed[blobId] = JmapEmailCodec.BuildEmail(
                    message,
                    options,
                    blob.SourceId,
                    uploadedBlobId: blobId,
                    rawSize: blob.Content.LongLength,
                    blobPartPrefix: blob.PartPrefix);
            }
            catch (FormatException)
            {
                notParsable.Add(blobId);
            }
        }
        return new JmapMethodResponse(Name, new JsonObject
        {
            ["accountId"] = accountId,
            ["parsed"] = parsed.Count == 0 ? null : parsed,
            ["notParsable"] = notParsable.Count == 0 ? null : notParsable,
            ["notFound"] = notFound.Count == 0 ? null : notFound,
        });
    }
}
