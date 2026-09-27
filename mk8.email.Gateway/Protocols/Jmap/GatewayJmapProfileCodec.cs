using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapProfileCodec
{
    private const string Core = GatewayJmapFeatureCodec.CoreCapability;
    private const string Mail = GatewayJmapFeatureCodec.MailCapability;
    private const string Submission = GatewayJmapFeatureCodec.SubmissionCapability;
    private const string Vacation = GatewayJmapFeatureCodec.VacationResponseCapability;
    private const string Contacts = GatewayJmapFeatureCodec.ContactsCapability;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonObject Render(JmapApplicationProfile profile, EnvironmentConfig environment)
    {
        var baseUrl = environment.Jmap.GetPublicBaseUri(environment.Smtp.Hostname).AbsoluteUri.TrimEnd('/');
        var session = new JsonObject
        {
            ["capabilities"] = Capabilities(profile.Limits, environment.Jmap),
            ["accounts"] = Accounts(profile),
            ["primaryAccounts"] = PrimaryAccounts(profile),
            ["username"] = profile.Username,
            ["apiUrl"] = $"{baseUrl}/jmap/api",
            ["downloadUrl"] = $"{baseUrl}/jmap/download/{{accountId}}/{{blobId}}/{{name}}?accept={{type}}",
            ["uploadUrl"] = $"{baseUrl}/jmap/upload/{{accountId}}",
            ["eventSourceUrl"] = $"{baseUrl}/jmap/event?types={{types}}&closeafter={{closeafter}}&ping={{ping}}",
        };
        var sanitized = GatewayJmapJson.SanitizeResponse(session);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sanitized.ToJsonString(JsonOptions)));
        sanitized["state"] = "S" + Convert.ToHexStringLower(digest.AsSpan(0, 12));
        return sanitized;
    }

    private static JsonObject Capabilities(JmapServiceLimits limits, JmapConfig gateway) => new()
    {
        [Core] = new JsonObject
        {
            ["maxSizeUpload"] = Math.Min(limits.MaxUploadSizeBytes, gateway.MaxUploadSizeBytes),
            ["maxConcurrentUpload"] = limits.MaxConcurrentUploads,
            ["maxSizeRequest"] = Math.Min(limits.MaxRequestSizeBytes, gateway.MaxRequestSizeBytes),
            ["maxConcurrentRequests"] = limits.MaxConcurrentRequests,
            ["maxCallsInRequest"] = limits.MaxCallsInRequest,
            ["maxObjectsInGet"] = limits.MaxObjectsInGet,
            ["maxObjectsInSet"] = limits.MaxObjectsInSet,
            ["collationAlgorithms"] = Strings(limits.CollationAlgorithms),
        },
        [Mail] = new JsonObject(),
        [Submission] = new JsonObject(),
        [Vacation] = new JsonObject(),
        [Contacts] = new JsonObject(),
    };

    private static JsonObject Accounts(JmapApplicationProfile profile)
    {
        var result = new JsonObject();
        foreach (var account in profile.Accounts)
        {
            var capabilities = AccountCapabilities(profile.Limits);
            if (account.SupportsContacts)
                capabilities[Contacts] = new JsonObject { ["maxAddressBooksPerCard"] = 1, ["mayCreateAddressBook"] = true };
            result[account.Id] = new JsonObject
            {
                ["name"] = account.Name,
                ["isPersonal"] = account.IsPersonal,
                ["isReadOnly"] = account.IsReadOnly,
                ["accountCapabilities"] = capabilities,
            };
        }
        return result;
    }

    private static JsonObject AccountCapabilities(JmapServiceLimits limits) => new()
    {
        [Mail] = new JsonObject
        {
            ["maxMailboxesPerEmail"] = 1,
            ["maxMailboxDepth"] = limits.MaxMailboxDepth,
            ["maxSizeMailboxName"] = limits.MaxSizeMailboxName,
            ["maxSizeAttachmentsPerEmail"] = limits.MaxMessageSizeBytes,
            ["emailQuerySortOptions"] = Strings(limits.EmailQuerySortOptions),
            ["mayCreateTopLevelMailbox"] = true,
        },
        [Submission] = new JsonObject
        {
            ["maxDelayedSend"] = 0,
            ["submissionExtensions"] = new JsonObject { ["SIZE"] = new JsonArray(limits.MaxMessageSizeBytes.ToString(CultureInfo.InvariantCulture)) },
        },
        [Vacation] = new JsonObject(),
    };

    private static JsonObject PrimaryAccounts(JmapApplicationProfile profile)
    {
        var result = new JsonObject();
        if (profile.Accounts.Length > 0)
        {
            var id = profile.Accounts[0].Id;
            result[Mail] = id;
            result[Submission] = id;
            result[Vacation] = id;
        }
        foreach (var account in profile.Accounts)
        {
            if (!account.SupportsContacts)
                continue;
            result[Contacts] = account.Id;
            break;
        }
        return result;
    }

    private static JsonArray Strings(string[] values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }
}
