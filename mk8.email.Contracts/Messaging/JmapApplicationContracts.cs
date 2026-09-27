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

public sealed record JmapSessionApplicationRequest(
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
    JmapApplicationInvocation[] Invocations,
    IReadOnlyDictionary<string, string>? CreatedIds = null);

public sealed record JmapApplicationInvocation(
    string Name,
    JsonObject Arguments,
    string CorrelationId);

public sealed record JmapApplicationBatchResult(
    JmapApplicationInvocation[] Invocations,
    string Revision,
    IReadOnlyDictionary<string, string>? CreatedIds = null);

public sealed record JmapUploadApplicationRequest(
    ProtocolAuthentication Authentication,
    string AccountId,
    string ContentType,
    byte[] Content);

public sealed record JmapDownloadApplicationRequest(
    ProtocolAuthentication Authentication,
    string AccountId,
    string BlobId);

public sealed record JmapEventApplicationRequest(
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
    JmapApplicationBatchResult? Batch = null);
