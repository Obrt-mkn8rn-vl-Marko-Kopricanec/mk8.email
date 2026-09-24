// Protocol request/result types are deliberately grouped in this transport-contract file.
#pragma warning disable MA0048
namespace mk8.email.Contracts.Mail;

public static class SmtpPresentationOperations
{
    public const string Protocol = "smtp";
    public const string Relay = "smtp.relay";
}

public sealed record SmtpRelayPresentationRequest(
    string Sender,
    string Recipient,
    string RawMessage,
    OutboundMailOptions? Options);
