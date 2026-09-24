namespace mk8.email.MailWire;

internal readonly record struct BoundedLine(string? Value, bool IsTooLong);
