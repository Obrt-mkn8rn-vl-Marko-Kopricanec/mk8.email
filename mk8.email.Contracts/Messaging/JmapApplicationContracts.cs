// Protocol request/result types are deliberately grouped in this transport-contract file; array fields are part of the established JSON/public API.
#pragma warning disable MA0048, CA1819
namespace mk8.email.Contracts.Messaging;

public static class ProtocolAuthenticationKinds
{
    public const string Password = "password";
    public const string BearerToken = "bearer-token";
}

public sealed record ProtocolAuthentication(
    string Kind,
    string? Username,
    string Secret);

public sealed record JmapProfileApplicationRequest(
    ProtocolAuthentication Authentication);

public sealed record JmapApplicationProfile(
    string Username,
    JmapServiceLimits Limits,
    JmapAccountProfile[] Accounts);

public sealed record JmapAccountProfile(
    string Id,
    string Name,
    bool IsPersonal,
    bool IsReadOnly,
    bool SupportsContacts);

public sealed record JmapServiceLimits(
    long MaxUploadSizeBytes,
    int MaxConcurrentUploads,
    long MaxRequestSizeBytes,
    int MaxConcurrentRequests,
    int MaxCallsInRequest,
    int MaxObjectsInGet,
    int MaxObjectsInSet,
    int MaxMailboxDepth,
    int MaxSizeMailboxName,
    long MaxMessageSizeBytes,
    string[] CollationAlgorithms,
    string[] EmailQuerySortOptions);

public sealed record JmapApplicationChanges(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AccountStates);

public sealed record JmapPushMessage(
    string? SubscriptionId = null,
    string? VerificationCode = null,
    JmapApplicationChanges? Changes = null);

public sealed record JmapUploadApplicationRequest(
    ProtocolAuthentication Authentication,
    string AccountId,
    string ContentType,
    byte[] Content);

public sealed record JmapDownloadApplicationRequest(
    ProtocolAuthentication Authentication,
    string AccountId,
    string BlobId);

public sealed record JmapChangesApplicationRequest(
    ProtocolAuthentication Authentication,
    long? AfterCursor,
    string[]? Types);

public static class JmapApplicationOutcomes
{
    public const string Ok = "ok";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not-found";
}

public sealed record JmapApplicationResult(
    string Outcome,
    byte[]? Content = null,
    string? ContentType = null,
    string? BlobId = null,
    long? Size = null,
    long? Cursor = null,
    MailApplicationFailure? Failure = null,
    MailOperationResult? OperationResult = null,
    JmapApplicationProfile? Profile = null,
    JmapApplicationChanges? Changes = null);
