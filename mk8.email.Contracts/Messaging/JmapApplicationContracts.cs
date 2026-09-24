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

public sealed record JmapSessionApplicationRequest(
    ProtocolAuthentication Authentication);

public sealed record JmapApiApplicationRequest(
    ProtocolAuthentication Authentication,
    byte[] Document);

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
    JmapApplicationProblem? Problem = null);
