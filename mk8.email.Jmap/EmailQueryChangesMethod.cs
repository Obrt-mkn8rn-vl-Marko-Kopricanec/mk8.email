using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailQueryChangesMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailboxMessageContentService content) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.FindMessageChanges;
    public string Capability => JmapConstants.MailCapability;

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
                "calculateTotal",
                "collapseThreads")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceQueryState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || !JmapMethodHelpers.TryGetOptionalId(arguments, "upToId", out _)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "collapseThreads", false, out var collapseThreads)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "calculateTotal", false, out var calculateTotal))
        {
            return JmapMethodResponse.Error("invalidArguments");
        }
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
            if (!JmapEmailQueryEngine.TryFilter(all, arguments["filter"], out var filtered, out var filterError))
                return JmapMethodResponse.Error(filterError);
            if (!JmapEmailQueryEngine.TryParseSort(arguments["sort"], out var sort, out var sortError))
                return JmapMethodResponse.Error(sortError);
            var changes = await states.GetChangesAsync(
                account.InboxId,
                JmapConstants.EmailDataType,
                sinceState,
                null,
                int.MaxValue,
                cancellationToken).ConfigureAwait(false);
            if (changes is null)
                return JmapMethodResponse.Error("cannotCalculateChanges");

            var ordered = JmapEmailQueryEngine.Sort(all, filtered, sort);
            if (collapseThreads)
                ordered = ordered.DistinctBy(item => item.ThreadId, StringComparer.Ordinal).ToList();
            var currentIds = ordered.Select(item => JmapId.Email(item.Email.Id)).ToList();
            var currentIdSet = currentIds.ToHashSet(StringComparer.Ordinal);
            var mutableFilter = JmapEmailQueryEngine.UsesMutableFilter(arguments["filter"]);
            var mutableSort = JmapEmailQueryEngine.UsesMutableSort(sort);
            var threadProperties = JmapEmailQueryEngine.UsesThreadProperties(
                arguments["filter"],
                sort);
            var hasMembershipChanges = changes.Created.Count > 0 || changes.Destroyed.Count > 0;
            var resetQuery = threadProperties
                || collapseThreads && (hasMembershipChanges || mutableFilter && changes.Updated.Count > 0);

            string[] removed;
            HashSet<string> addedIds;
            if (resetQuery)
            {
                var createdIds = changes.Created.ToHashSet(StringComparer.Ordinal);
                var oldCandidates = mutableFilter
                    ? all.Select(item => JmapId.Email(item.Email.Id))
                    : JmapEmailQueryEngine.Sort(all, filtered, sort)
                        .Select(item => JmapId.Email(item.Email.Id));
                removed = oldCandidates
                    .Where(id => !createdIds.Contains(id))
                    .Concat(changes.Destroyed)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                addedIds = currentIdSet;
            }
            else
            {
                var includeUpdates = mutableFilter || mutableSort;
                removed = changes.Destroyed
                    .Concat(includeUpdates ? changes.Updated : [])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                addedIds = changes.Created
                    .Concat(includeUpdates ? changes.Updated : [])
                    .Where(currentIdSet.Contains)
                    .ToHashSet(StringComparer.Ordinal);
            }
            var added = currentIds
                .Select((id, index) => new { Id = id, Index = index })
                .Where(item => addedIds.Contains(item.Id))
                .ToArray();
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
            if (calculateTotal)
                response["total"] = currentIds.Count;
            return new JmapMethodResponse(Operation, response);
        }
        finally
        {
            foreach (var item in all)
                item.Dispose();
        }
    }
}
