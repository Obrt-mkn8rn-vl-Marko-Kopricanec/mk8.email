namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayHeaderUrl
{
    public static bool IsValidParsedForm(string value) =>
        !string.IsNullOrEmpty(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && value.IndexOfAny(['<', '>', '\r', '\n']) < 0
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.IsWellFormedOriginalString();
}
