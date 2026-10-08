using System.Globalization;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemCreate
{
    private static readonly string[] DraftKeywords = ["$draft"];

    public static async Task<string> ExecuteAsync(GatewayEwsClient application, ProtocolAuthentication authentication,
        JmapApplicationProfile profile, Guid account, GatewayEwsRequest request, CancellationToken cancellationToken)
    {
        var contents = request.MimeCreates ?? throw new InvalidOperationException("The EWS create content is missing.");
        if (contents.Count is <= 0 or > GatewayEwsRequestParser.MaximumReferences || contents.Count > profile.Limits.MaxObjectsInSet)
            throw new GatewayEwsRequestException("ErrorExceededFindCountLimit");
        if (request.Folders.Count != 1) throw new InvalidOperationException("The EWS create destination is missing.");
        var destination = request.Folders[0];
        var code = !GatewayEwsFolderIdCodec.TryDecode(destination.Id, out var owner, out var target) ? "ErrorInvalidIdMalformed"
            : account == Guid.Empty ? "ErrorFolderNotFound" : owner != account ? "ErrorAccessDenied"
            : target == Guid.Empty ? "ErrorAccessDenied" : null;
        var maximum = Math.Min(GatewayEwsItemCreateParser.MaximumMimeBytes, Math.Min(profile.Limits.MaxUploadSizeBytes,
            Math.Min(profile.Limits.MaxMessageSizeBytes, GatewayHttpPayloadBudget.MaximumBinaryBodyBytes(application.MaximumPayloadBytes) / 2)));
        var plans = contents.Select((content, index) => new GatewayEwsItemCreatePlan("ewsCreate" + index.ToString(CultureInfo.InvariantCulture), content,
            code ?? (content.Length == 0 || content.Length > maximum ? "ErrorDataSizeLimitExceeded" : null))).ToArray();
        if (plans.All(plan => plan.Code is not null)) return GatewayEwsItemCreateResponse.Render(account, plans, null);
        var state = await application.ReadMessageStateAsync(authentication, profile, account, cancellationToken).ConfigureAwait(false);
        if (state is null) return GatewayEwsItemCreateResponse.Render(account, plans, null, "ErrorFolderNotFound");
        var imports = new List<MailImportItem>();
        foreach (var plan in plans)
        {
            if (plan.Code is not null) continue;
            var blob = await application.UploadMimeAsync(authentication, account, plan.Content, cancellationToken).ConfigureAwait(false);
            imports.Add(new(plan.Token, blob, false, target, MailMessageMailboxIssue.None, DraftKeywords,
                MailMessageKeywordIssue.None, null, false));
        }
        // Uploads are account-scoped temporary Azure objects, not domain creates.
        // Only the existing import transaction/receipt creates stored messages;
        // it always compares the admitted state and independently reauthorizes
        // the current destination. Unused uploads retain normal quota/expiry.
        var sizes = plans.Where(plan => plan.Code is null).ToDictionary(plan => plan.Token, plan => plan.Content.Length, StringComparer.Ordinal);
        var command = new MailImportCommand(account, state, imports);
        var reply = await application.ExecuteOperationAsync(authentication, profile, MailOperationKind.ImportMessages,
            command, cancellationToken).ConfigureAwait(false);
        return GatewayEwsItemCreateResponse.Render(account, plans, GatewayEwsItemCreateReply.Decode(reply, command, sizes));
    }
}
