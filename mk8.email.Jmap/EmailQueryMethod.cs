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

internal sealed class EmailQueryMethod(
    EmailDbContext database,
    JmapAccountService accounts,
    JmapStateService states,
    MailboxMessageContentService content,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.FindMessages;
    public MailFeature Feature => MailFeature.Messages;

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
                "position",
                "anchor",
                "anchorOffset",
                "limit",
                "calculateTotal",
                "collapseThreads")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "collapseThreads", false, out var collapseThreads)
            || !JmapMethodHelpers.TryGetQueryWindow(
                arguments,
                out var position,
                out var anchor,
                out var anchorOffset)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "limit", out var requestedLimit)
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
            var ordered = JmapEmailQueryEngine.Sort(all, filtered, sort);
            if (collapseThreads)
                ordered = ordered.DistinctBy(item => item.ThreadId, StringComparer.Ordinal).ToList();
            var ids = ordered.Select(item => JmapId.Email(item.Email.Id)).ToList();

            if (anchor is not null)
            {
                var anchorIndex = ids.IndexOf(anchor);
                if (anchorIndex < 0)
                    return JmapMethodResponse.Error("anchorNotFound");
                position = Math.Min(
                    JmapMethodHelpers.MaximumInt,
                    Math.Max(0L, anchorIndex + anchorOffset));
            }
            else if (position < 0)
            {
                position = Math.Max(0L, ids.Count + position);
            }

            var enforcedLimit = JmapMethodHelpers.ClampToServerLimit(
                requestedLimit,
                environment.Jmap.MaxObjectsInGet);
            var pagePosition = position >= ids.Count ? ids.Count : checked((int)position);
            List<string> page = pagePosition >= ids.Count
                ? []
                : ids.Skip(pagePosition).Take(enforcedLimit).ToList();
            var response = new JsonObject
            {
                ["accountId"] = accountId,
                ["queryState"] = await states.GetStateAsync(
                    account.InboxId,
                    JmapConstants.EmailDataType,
                    cancellationToken).ConfigureAwait(false),
                ["canCalculateChanges"] = true,
                ["position"] = position,
                ["ids"] = JmapMethodHelpers.ToJsonArray(page),
            };
            if (calculateTotal)
                response["total"] = ids.Count;
            if (requestedLimit is null || requestedLimit > enforcedLimit)
                response["limit"] = enforcedLimit;
            return new JmapMethodResponse(Operation, response);
        }
        finally
        {
            foreach (var item in all)
                item.Dispose();
        }
    }
}
