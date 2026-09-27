using mk8.email.Contracts.Messaging;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed class SearchSnippetGetMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    MailboxMessageContentService content,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.ReadSearchSnippets;
    public string Capability => JmapConstants.MailCapability;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "filter", "emailIds")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapEmailArguments.TryGetIds(arguments, "emailIds", false, out var emailIds)
            || emailIds is null)
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
        if (emailIds.Count > environment.Jmap.MaxObjectsInGet)
            return JmapMethodResponse.Error("requestTooLarge");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return JmapMethodResponse.Error("accountNotFound");

        var all = await JmapEmailQueryEngine.LoadAsync(
            database,
            content,
            account.InboxId,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (!JmapEmailQueryEngine.TryFilter(
                    all,
                    arguments["filter"],
                    out _,
                    out var filterError))
            {
                return JmapMethodResponse.Error(filterError == "invalidArguments"
                    ? "unsupportedFilter"
                    : filterError);
            }
            var byId = all.ToDictionary(item => JmapId.Email(item.Email.Id), StringComparer.Ordinal);
            var terms = JmapSearchSnippetFormatter.ExtractTerms(arguments["filter"]);
            var list = new JsonArray();
            var notFound = new JsonArray();
            foreach (var id in emailIds.Distinct(StringComparer.Ordinal))
            {
                if (!byId.TryGetValue(id, out var item))
                {
                    notFound.Add(id);
                    continue;
                }
                list.Add(new JsonObject
                {
                    ["emailId"] = id,
                    ["subject"] = JmapSearchSnippetFormatter.HighlightSubject(
                        JmapEmailCodec.LastTextHeader(item.Message, "Subject"),
                        terms),
                    ["preview"] = JmapSearchSnippetFormatter.HighlightPreview(
                        JmapSearchSnippetFormatter.PlainBody(item.Message),
                        terms),
                });
            }
            return new JmapMethodResponse(Operation, new JsonObject
            {
                ["accountId"] = accountId,
                ["list"] = list,
                ["notFound"] = notFound.Count == 0 ? null : notFound,
            });
        }
        finally
        {
            foreach (var item in all)
                item.Dispose();
        }
    }
}
