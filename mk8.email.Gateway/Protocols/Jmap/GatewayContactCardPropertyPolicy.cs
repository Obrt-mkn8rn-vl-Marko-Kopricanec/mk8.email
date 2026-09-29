namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayContactCardPropertyPolicy
{
    private static readonly HashSet<string> CardProperties = new(
    [
        "@type", "version", "created", "kind", "language", "members", "prodId",
        "relatedTo", "uid", "updated", "name", "nicknames", "organizations",
        "speakToAs", "titles", "emails", "onlineServices", "phones",
        "preferredLanguages", "calendars", "schedulingAddresses", "addresses",
        "cryptoKeys", "directories", "links", "media", "localizations",
        "anniversaries", "keywords", "notes", "personalInfo",
    ], StringComparer.Ordinal);

    private static readonly HashSet<string> KnownProperties = new(
    [
        "@type", "address", "addresses", "anniversaries", "author", "calendarScale",
        "calendars", "components", "contexts", "coordinates", "countryCode", "created",
        "cryptoKeys", "date", "day", "defaultSeparator", "directories", "emails",
        "features", "full", "grammaticalGender", "isOrdered", "keywords", "kind",
        "label", "language", "level", "links", "listAs", "localizations", "media",
        "mediaType", "members", "month", "name", "nicknames", "note", "notes",
        "number", "onlineServices", "organizationId", "organizations", "personalInfo",
        "phones", "phonetic", "phoneticScript", "phoneticSystem", "place", "pref",
        "preferredLanguages", "prodId", "pronouns", "relatedTo", "relation",
        "schedulingAddresses", "service", "sortAs", "speakToAs", "timeZone", "titles",
        "uid", "units", "updated", "uri", "user", "utc", "value", "version", "year",
        "id", "addressBookIds", "blobId", "extra",
    ], StringComparer.Ordinal);

    public static bool IsSupported(string property) =>
        property is "id" or "addressBookIds"
        || CardProperties.Contains(property)
        || !KnownProperties.Contains(property)
            && !KnownProperties.Any(known => string.Equals(known, property, StringComparison.OrdinalIgnoreCase))
            && IsValidExtensionProperty(property);

    private static bool IsValidExtensionProperty(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (colon == 0 || colon == value.Length - 1)
                return false;
            var labels = value[..colon].Split('.');
            if (labels.Any(label => label.Length == 0
                    || !IsVendorAlphaNumeric(label[0])
                    || !IsVendorAlphaNumeric(label[^1])
                    || label.Any(character => !IsVendorAlphaNumeric(character) && character != '-')))
                return false;
            return value[(colon + 1)..].All(character =>
                character is ' ' or '\t' or '!'
                || character is >= '#' and <= '.'
                || character is >= '0' and <= '}'
                || character > 0x7f);
        }
        return value.Length > 0
            && value[0] is >= 'a' and <= 'z'
            && value.All(char.IsAsciiLetterOrDigit);
    }

    private static bool IsVendorAlphaNumeric(char value) =>
        char.IsAsciiLetterOrDigit(value) || value > 0x7f;
}
