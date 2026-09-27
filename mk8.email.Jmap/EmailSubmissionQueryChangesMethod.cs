using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailSubmissionQueryChangesMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.FindSubmissionChanges;
    public MailFeature Feature => MailFeature.Submission;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(
                arguments,
                "accountId",
                "filter",
                "sort",
                "sinceQueryState",
                "maxChanges",
                "upToId",
                "calculateTotal")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || !JmapMethodHelpers.TryGetOptionalId(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var all = await database.JmapEmailSubmissions.AsNoTracking()
            .Where(item => item.AccountId == account.InboxId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!JmapEmailSubmissionQueryEngine.TryFilter(all, arguments["filter"], out var filtered, out var filterError))
            return JmapMethodResponse.Error(filterError);
        if (!JmapEmailSubmissionQueryEngine.TrySort(arguments["sort"], out var sort, out var sortError))
            return JmapMethodResponse.Error(sortError);
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.EmailSubmissionDataType,
            sinceState,
            null,
            int.MaxValue,
            cancellationToken).ConfigureAwait(false);
        if (changes is null)
            return JmapMethodResponse.Error("cannotCalculateChanges");
        var currentIds = JmapEmailSubmissionQueryEngine.Sort(filtered, sort)
            .Select(item => JmapId.Submission(item.Id))
            .ToList();
        var createdIds = changes.Created.ToHashSet(StringComparer.Ordinal);
        var added = currentIds
            .Select((id, index) => new { Id = id, Index = index })
            .Where(item => createdIds.Contains(item.Id))
            .ToArray();
        var removed = changes.Destroyed.Distinct(StringComparer.Ordinal).ToArray();
        if (maxChanges is not null && removed.LongLength + added.LongLength > maxChanges.Value)
            return JmapMethodResponse.Error("tooManyChanges");
        var response = new JsonObject
        {
            ["accountId"] = accountId,
            ["oldQueryState"] = sinceState,
            ["newQueryState"] = changes.NewState,
            ["removed"] = JmapMethodHelpers.ToJsonArray(removed),
            ["added"] = new JsonArray(added
                .Select(item => (JsonNode)new JsonObject
                {
                    ["id"] = item.Id,
                    ["index"] = item.Index,
                })
                .ToArray()),
        };
        if (calculateTotal) response["total"] = currentIds.Count;
        return new JmapMethodResponse(Operation, response);
    }
}
