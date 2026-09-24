namespace mk8.email.Configuration;

public static class DkimIdentityValidator
{
    public static bool IsValidDomain(string domain) =>
        domain is not null
        && Uri.CheckHostName(domain) == UriHostNameType.Dns
        && domain.Contains('.', StringComparison.Ordinal);

    public static bool IsValidSelector(string selector) =>
        selector is not null
        && selector.Length is >= 1 and <= 63
        && selector[0] != '-'
        && selector[^1] != '-'
        && selector.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
