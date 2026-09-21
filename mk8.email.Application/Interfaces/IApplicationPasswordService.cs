namespace mk8.email.Application.Interfaces;

public sealed record ApplicationPasswordCreationResult(
    bool Succeeded,
    string Message,
    Guid? Id = null,
    string? Password = null);

public sealed record ApplicationPasswordSummary(
    Guid Id,
    string Name,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);

public interface IApplicationPasswordService
{
    Task<ApplicationPasswordCreationResult> CreateAsync(
        string username,
        string name,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationPasswordSummary>> ListAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(
        string username,
        Guid applicationPasswordId,
        CancellationToken cancellationToken = default);
}
