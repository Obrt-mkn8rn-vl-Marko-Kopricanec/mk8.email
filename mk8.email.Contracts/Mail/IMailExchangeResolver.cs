namespace mk8.email.Contracts.Mail;

public enum MailRoutingStatus
{
    Available,
    DoesNotAcceptMail,
    TemporaryFailure,
}

public sealed record MailExchangeEndpoint(string Host, ushort Preference, int Port = 25);

public sealed record MailRoutingResult(
    MailRoutingStatus Status,
    IReadOnlyList<MailExchangeEndpoint> Exchanges);

public interface IMailExchangeResolver
{
    Task<MailRoutingResult> ResolveAsync(string domain, CancellationToken cancellationToken);
}
