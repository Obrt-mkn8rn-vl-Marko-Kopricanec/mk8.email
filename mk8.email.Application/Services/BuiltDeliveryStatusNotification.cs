namespace mk8.email.Application.Services;

internal sealed record BuiltDeliveryStatusNotification(
    string RawMessage,
    bool RequiresSmtpUtf8);
