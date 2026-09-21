using System.Security.Cryptography;
using System.Text.Json.Nodes;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

public sealed record JmapSessionDocument(JsonObject Value, string State);

public sealed class JmapSessionService(
    JmapAccountService accounts,
    JmapContactStore contacts,
    EnvironmentConfig environment)
{
    public async Task<JmapSessionDocument> BuildAsync(
        AuthenticatedMailUser user,
        CancellationToken cancellationToken = default)
    {
        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken);
        var accessibleAccounts = await accounts.GetAccountsAsync(user, cancellationToken);
        var baseUrl = environment.Jmap.GetPublicBaseUri(environment.Smtp.Hostname)
            .AbsoluteUri
            .TrimEnd('/');

        var capability = new JsonObject
        {
            ["maxSizeUpload"] = environment.Jmap.MaxUploadSizeBytes,
            ["maxConcurrentUpload"] = environment.Jmap.MaxConcurrentUploads,
            ["maxSizeRequest"] = environment.Jmap.MaxRequestSizeBytes,
            ["maxConcurrentRequests"] = environment.Jmap.MaxConcurrentRequests,
            ["maxCallsInRequest"] = environment.Jmap.MaxCallsInRequest,
            ["maxObjectsInGet"] = environment.Jmap.MaxObjectsInGet,
            ["maxObjectsInSet"] = environment.Jmap.MaxObjectsInSet,
            ["collationAlgorithms"] = JmapMethodHelpers.ToJsonArray(
                JmapCollation.SupportedIdentifiers),
        };
        var capabilities = new JsonObject
        {
            [JmapConstants.CoreCapability] = capability,
            [JmapConstants.MailCapability] = new JsonObject(),
            [JmapConstants.SubmissionCapability] = new JsonObject(),
            [JmapConstants.VacationResponseCapability] = new JsonObject(),
            [JmapConstants.ContactsCapability] = new JsonObject(),
        };

        var accountValues = new JsonObject();
        var mailAccountCapability = new JsonObject
        {
            ["maxMailboxesPerEmail"] = 1,
            ["maxMailboxDepth"] = FolderDB.MaximumHierarchyDepth,
            ["maxSizeMailboxName"] = FolderDB.MaximumLeafNameOctets,
            ["maxSizeAttachmentsPerEmail"] = environment.Limits.MaxMessageSizeBytes,
            ["emailQuerySortOptions"] = new JsonArray(
                "receivedAt",
                "size",
                "from",
                "to",
                "subject",
                "sentAt",
                "hasKeyword",
                "allInThreadHaveKeyword",
                "someInThreadHaveKeyword"),
            ["mayCreateTopLevelMailbox"] = true,
        };
        var submissionAccountCapability = new JsonObject
        {
            ["maxDelayedSend"] = 0,
            ["submissionExtensions"] = new JsonObject
            {
                ["SIZE"] = new JsonArray(environment.Limits.MaxMessageSizeBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            },
        };
        for (var accountIndex = 0; accountIndex < accessibleAccounts.Count; accountIndex++)
        {
            var account = accessibleAccounts[accountIndex];
            var accountCapabilities = new JsonObject
            {
                [JmapConstants.MailCapability] = mailAccountCapability.DeepClone(),
                [JmapConstants.SubmissionCapability] = submissionAccountCapability.DeepClone(),
                [JmapConstants.VacationResponseCapability] = new JsonObject(),
            };
            if (accountIndex == 0)
            {
                accountCapabilities[JmapConstants.ContactsCapability] = new JsonObject
                {
                    ["maxAddressBooksPerCard"] = 1,
                    ["mayCreateAddressBook"] = true,
                };
            }
            accountValues[JmapId.Account(account.InboxId)] = new JsonObject
            {
                ["name"] = account.Address,
                ["isPersonal"] = true,
                ["isReadOnly"] = false,
                ["accountCapabilities"] = accountCapabilities,
            };
        }

        var primaryAccounts = new JsonObject();
        if (accessibleAccounts.Count > 0)
        {
            var primaryId = JmapId.Account(accessibleAccounts[0].InboxId);
            primaryAccounts[JmapConstants.MailCapability] = primaryId;
            primaryAccounts[JmapConstants.SubmissionCapability] = primaryId;
            primaryAccounts[JmapConstants.VacationResponseCapability] = primaryId;
            primaryAccounts[JmapConstants.ContactsCapability] = primaryId;
        }

        var session = new JsonObject
        {
            ["capabilities"] = capabilities,
            ["accounts"] = accountValues,
            ["primaryAccounts"] = primaryAccounts,
            ["username"] = user.Username,
            ["apiUrl"] = $"{baseUrl}/jmap/api",
            ["downloadUrl"] = $"{baseUrl}/jmap/download/{{accountId}}/{{blobId}}/{{name}}?accept={{type}}",
            ["uploadUrl"] = $"{baseUrl}/jmap/upload/{{accountId}}",
            ["eventSourceUrl"] = $"{baseUrl}/jmap/event?types={{types}}&closeafter={{closeafter}}&ping={{ping}}",
        };
        var sanitizedSession = JmapJson.SanitizeResponse(session);
        var state = BuildState(sanitizedSession);
        sanitizedSession["state"] = state;
        return new JmapSessionDocument(sanitizedSession, state);
    }

    private static string BuildState(JsonObject session)
    {
        var digest = SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(session.ToJsonString(JmapJson.SerializerOptions)));
        return "S" + Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }
}
