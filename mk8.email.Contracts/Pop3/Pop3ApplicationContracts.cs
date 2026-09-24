// Protocol request/result types are deliberately grouped in this transport-contract file; concrete collection types are part of the established JSON/public API.
#pragma warning disable MA0048, CA1002, CA1819, MA0016
namespace mk8.email.Contracts.Pop3;

public interface IPop3ApplicationService
{
    Task<Pop3IdentityResult> AuthenticatePasswordAsync(
        Pop3PasswordAuthentication request,
        CancellationToken cancellationToken = default);

    Task<Pop3IdentityResult> AuthenticateOAuthAsync(
        Pop3OAuthAuthentication request,
        CancellationToken cancellationToken = default);

    Task<Pop3MaildropSnapshot> ListMaildropAsync(
        Pop3UserRequest request,
        CancellationToken cancellationToken = default);

    Task<Pop3MessageResult> GetMessageAsync(
        Pop3MessageRequest request,
        CancellationToken cancellationToken = default);

    Task<Pop3DeleteResult> CommitDeletesAsync(
        Pop3DeleteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record Pop3PasswordAuthentication(string Username, string Password);

public sealed record Pop3OAuthAuthentication(string Username, string AccessToken);

public sealed record Pop3IdentityResult(Guid? UserId, string? Username);

public sealed record Pop3UserRequest(Guid UserId);

public sealed record Pop3MessageRequest(Guid UserId, Guid MessageId);

public sealed record Pop3DeleteRequest(Guid UserId, Guid[] MessageIds);

public sealed record Pop3MessageSummary(Guid Id, int Uid, int SizeBytes);

public sealed record Pop3MaildropSnapshot(List<Pop3MessageSummary> Messages);

public sealed record Pop3MessageResult(byte[]? RawMessage);

public sealed record Pop3DeleteResult(int DeletedCount);
