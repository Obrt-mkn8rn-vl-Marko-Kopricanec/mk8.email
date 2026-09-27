// Protocol request/result types are deliberately grouped in this transport-contract file; array fields are part of the established JSON/public API.
#pragma warning disable MA0048, CA1819
using System.Text.Json.Nodes;

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

// A null batch is an authentication/resource check for a document rejected by
// Gateway. No malformed document or presentation exception enters Worker.
public sealed record JmapBatchApplicationRequest(
    ProtocolAuthentication Authentication,
    JmapApplicationBatch? Batch,
    JmapBatchPreflight? Preflight = null);

public sealed record JmapBatchPreflight(
    string[] Capabilities,
    int InvocationCount);

public sealed record JmapApplicationBatch(
    string[] Capabilities,
    JmapApplicationCall[] Invocations,
    IReadOnlyDictionary<string, string>? CreatedIds = null);

public sealed record JmapApplicationCall(
    string Name,
    JsonObject Arguments,
    string CorrelationId,
    ApplicationArgumentBinding[]? Bindings = null);

// Gateway translates wire syntax into ordered application data dependencies.
// A deferred preparation failure stays at its original dependency position.
public enum ApplicationBindingFailure
{
    None,
    InvalidTarget,
    InvalidSource,
}

public sealed record ApplicationArgumentBinding(
    string Target,
    string SourceCorrelationId,
    string SourceName,
    ApplicationValuePathSegment[] Path,
    ApplicationBindingFailure Failure = ApplicationBindingFailure.None);

// A result's shape determines whether a segment selects an object property,
// one array item, or every array item. No wire pointer is interpreted by Worker.
public sealed record ApplicationValuePathSegment(
    string Property,
    int? ArrayIndex = null,
    bool AllArrayItems = false);

public sealed record JmapApplicationInvocation(
    string Name,
    JsonObject Arguments,
    string CorrelationId);

public sealed record JmapApplicationBatchResult(
    JmapApplicationInvocation[] Invocations,
    JmapApplicationProfile Profile,
    IReadOnlyDictionary<string, string>? CreatedIds = null);

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

public sealed record JmapApplicationProblem(
    string Type,
    string Title,
    string? Detail = null,
    string? Limit = null);

public sealed record JmapApplicationResult(
    string Outcome,
    byte[]? Content = null,
    string? ContentType = null,
    string? BlobId = null,
    long? Size = null,
    long? Cursor = null,
    JmapApplicationProblem? Problem = null,
    JmapApplicationBatchResult? Batch = null,
    JmapApplicationProfile? Profile = null,
    JmapApplicationChanges? Changes = null);
