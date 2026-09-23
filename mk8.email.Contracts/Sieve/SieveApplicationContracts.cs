namespace mk8.email.Contracts.Sieve;

public interface ISieveApplicationService
{
    Task<SieveIdentityResult> AuthenticatePasswordAsync(
        SievePasswordAuthentication request,
        CancellationToken cancellationToken = default);

    Task<SieveIdentityResult> AuthenticateOAuthAsync(
        SieveOAuthAuthentication request,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> CheckSpaceAsync(
        SieveCheckSpaceRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
        SieveUserRequest request,
        CancellationToken cancellationToken = default);

    Task<SieveStoredScriptResult> GetAsync(
        SieveNamedRequest request,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> PutAsync(
        SievePutRequest request,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> SetActiveAsync(
        SieveSetActiveRequest request,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> DeleteAsync(
        SieveNamedRequest request,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> RenameAsync(
        SieveRenameRequest request,
        CancellationToken cancellationToken = default);

    Task<SieveValidationResult> ValidateAsync(
        SieveValidationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SievePasswordAuthentication(string Username, string Password);

public sealed record SieveOAuthAuthentication(string AccessToken);

public sealed record SieveIdentityResult(Guid? UserId, string? Username);

public sealed record SieveUserRequest(Guid UserId);

public sealed record SieveNamedRequest(Guid UserId, string Name);

public sealed record SieveCheckSpaceRequest(Guid UserId, string Name, long ContentSizeBytes, int MaximumScripts);

public sealed record SievePutRequest(Guid UserId, string Name, string Content, int MaximumScripts);

public sealed record SieveSetActiveRequest(Guid UserId, string? Name);

public sealed record SieveRenameRequest(Guid UserId, string OldName, string NewName);

public sealed record SieveValidationRequest(string Content);

public sealed record SieveValidationResult(bool Succeeded, SieveDiagnosticResult? Diagnostic);

public sealed record SieveDiagnosticResult(int Line, int Column, string Message);

public sealed record SieveScriptSummary(string Name, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record StoredSieveScript(
    string Name,
    string Content,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SieveStoredScriptResult(StoredSieveScript? Script);

public sealed record SieveScriptOperationResult(
    bool Succeeded,
    string? Error = null,
    string? ResponseCode = null);
