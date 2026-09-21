using mk8.email.Application.Protocol;

namespace mk8.email.Application.Interfaces;

internal sealed record SieveScriptSummary(
    string Name,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record StoredSieveScript(
    string Name,
    string Content,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record SieveScriptOperationResult(
    bool Succeeded,
    string? Error = null,
    string? ResponseCode = null);

internal interface ISieveScriptService
{
    SieveCompilationResult Validate(string content);

    Task<SieveScriptOperationResult> CheckSpaceAsync(
        Guid userId,
        string name,
        long contentSizeBytes,
        int maximumScripts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<StoredSieveScript?> GetAsync(
        Guid userId,
        string name,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> PutAsync(
        Guid userId,
        string name,
        string content,
        int maximumScripts = int.MaxValue,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> SetActiveAsync(
        Guid userId,
        string? name,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> DeleteAsync(
        Guid userId,
        string name,
        CancellationToken cancellationToken = default);

    Task<SieveScriptOperationResult> RenameAsync(
        Guid userId,
        string oldName,
        string newName,
        CancellationToken cancellationToken = default);
}
