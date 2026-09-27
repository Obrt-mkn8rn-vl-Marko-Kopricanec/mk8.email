using mk8.email.Contracts.Messaging;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class EmailSubmissionChangesMethod(
    JmapAccountService accounts,
    JmapStateService states,
    EnvironmentConfig environment) : IJmapMethod
{
    public MailOperationKind Operation => MailOperationKind.ReadSubmissionChanges;
    public MailFeature Feature => MailFeature.Submission;

    public async Task<JmapMethodResponse> InvokeAsync(
        JmapInvocationContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!JmapMethodHelpers.HasOnlyProperties(arguments, "accountId", "sinceState", "maxChanges")
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "accountId", out var accountId)
            || !JmapMethodHelpers.TryGetRequiredString(arguments, "sinceState", out var sinceState)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(arguments, "maxChanges", out var maxChanges)
            || maxChanges == 0)
            return JmapMethodResponse.Error("invalidArguments");
        var account = await accounts.GetAccountAsync(context.User, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return JmapMethodResponse.Error("accountNotFound");
        var changes = await states.GetChangesAsync(
            account.InboxId,
            JmapConstants.EmailSubmissionDataType,
            sinceState,
            maxChanges,
            environment.Jmap.MaxObjectsInGet,
            cancellationToken).ConfigureAwait(false);
        if (changes is null) return JmapMethodResponse.Error("cannotCalculateChanges");
        return new JmapMethodResponse(Operation, new JsonObject
        {
            ["accountId"] = accountId,
            ["oldState"] = changes.OldState,
            ["newState"] = changes.NewState,
            ["hasMoreChanges"] = changes.HasMoreChanges,
            ["created"] = JmapMethodHelpers.ToJsonArray(changes.Created),
            ["updated"] = JmapMethodHelpers.ToJsonArray(changes.Updated),
            ["destroyed"] = JmapMethodHelpers.ToJsonArray(changes.Destroyed),
        });
    }
}
