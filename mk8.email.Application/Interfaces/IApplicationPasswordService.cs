namespace mk8.email.Application.Interfaces;

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
