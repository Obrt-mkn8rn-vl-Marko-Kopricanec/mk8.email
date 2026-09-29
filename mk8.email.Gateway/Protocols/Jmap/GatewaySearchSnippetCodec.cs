using System.Text.Json.Nodes;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewaySearchSnippetCodec
{
    internal sealed record Call(MailSearchSnippetCommand Command, string AccountId,
        IReadOnlyList<string> EmailIds, string? DeferredError);

    public static bool TryParse(JsonObject arguments, int maximumObjects,
        out Call? call, out string? failure)
    {
        call = null;
        failure = null;
        if (arguments.Any(item => item.Key is not ("accountId" or "filter" or "emailIds"))
            || arguments["accountId"] is not JsonValue accountNode
            || !accountNode.TryGetValue<string>(out var accountId) || accountId is null
            || arguments["emailIds"] is not JsonArray ids
            || ids.Any(item => item is not JsonValue value
                || !value.TryGetValue<string>(out var id) || id is null
                || !GatewayJmapBatchCodec.IsId(id)))
        {
            failure = "invalidArguments";
            return false;
        }
        if (ids.Count > maximumObjects)
        {
            failure = "requestTooLarge";
            return false;
        }
        _ = GatewayEmailQueryCodec.TryParseFilter(arguments["filter"], out _, out var filterError);
        var distinct = ids.Select(item => item!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray();
        var parsed = distinct.Select(ParseEmail).Where(id => id != Guid.Empty).Distinct().ToArray();
        var account = accountId.Length == 33 && accountId[0] == 'A'
            && Guid.TryParseExact(accountId.AsSpan(1), "N", out var accountIdValue)
                ? accountIdValue : Guid.Empty;
        call = new(new(account, filterError is not null, parsed,
                filterError is null ? GatewaySearchSnippetFormatter.ExtractTerms(arguments["filter"]) : []),
            accountId, distinct, filterError is "invalidArguments" ? "unsupportedFilter" : filterError);
        return true;
    }

    public static (MailOperationKind Operation, JsonObject Data) Render(Call call,
        MailSearchSnippetResult result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Snippets is null)
            throw new InvalidOperationException("The Application returned an invalid search snippet result.");
        if (result.Status == MailSearchSnippetStatus.AccountNotFound)
        {
            if (result.Snippets.Count != 0)
                throw new InvalidOperationException("The Application exposed snippets for an unavailable account.");
            return Error("accountNotFound");
        }
        if (call.DeferredError is not null)
        {
            if (result.Status != MailSearchSnippetStatus.Authorized || result.Snippets.Count != 0)
                throw new InvalidOperationException("The Application did not authorize deferred snippet errors.");
            return Error(call.DeferredError);
        }
        if (result.Status != MailSearchSnippetStatus.Ok)
            throw new InvalidOperationException("The Application returned an incomplete search snippet result.");
        var requested = call.Command.MessageIds.ToHashSet();
        var snapshots = new Dictionary<Guid, MailSearchSnippetSnapshot>();
        foreach (var snippet in result.Snippets)
        {
            if (snippet is null || snippet.MessageId == Guid.Empty
                || !requested.Contains(snippet.MessageId)
                || snippet.SubjectText?.Length > 4098
                || snippet.PreviewWindow?.Length > 182
                || !snapshots.TryAdd(snippet.MessageId, snippet))
                throw new InvalidOperationException("The Application returned an unexpected search snippet.");
        }
        var list = new JsonArray();
        var notFound = new JsonArray();
        foreach (var id in call.EmailIds)
        {
            var parsed = ParseEmail(id);
            if (parsed == Guid.Empty || !snapshots.TryGetValue(parsed, out var snippet)
                || !string.Equals(id, $"E{parsed:N}", StringComparison.Ordinal))
            {
                notFound.Add(id);
                continue;
            }
            list.Add(new JsonObject
            {
                ["emailId"] = id,
                ["subject"] = GatewaySearchSnippetFormatter.HighlightSubject(
                    snippet.SubjectText, call.Command.Terms),
                ["preview"] = GatewaySearchSnippetFormatter.HighlightPreviewWindow(
                    snippet.PreviewWindow, call.Command.Terms),
            });
        }
        return (MailOperationKind.ReadSearchSnippets, new JsonObject
        {
            ["accountId"] = call.AccountId,
            ["list"] = list,
            ["notFound"] = notFound.Count == 0 ? null : notFound,
        });
    }

    private static Guid ParseEmail(string id) => id.Length == 33 && id[0] == 'E'
        && Guid.TryParseExact(id.AsSpan(1), "N", out var parsed) ? parsed : Guid.Empty;

    private static (MailOperationKind Operation, JsonObject Data) Error(string type) =>
        (MailOperationKind.Failure, new JsonObject { ["type"] = type });
}
