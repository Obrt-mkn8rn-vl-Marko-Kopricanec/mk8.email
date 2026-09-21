using System.Text;

namespace mk8.email.Dav;

internal sealed record DavContentInfo(
    string Uid,
    string ContentType,
    IReadOnlySet<string> Components,
    string Text,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Properties);

internal static class DavContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryValidate(
        DavCollectionKind kind,
        string? requestContentType,
        byte[] content,
        out DavContentInfo? info,
        out string failure)
    {
        info = null;
        failure = string.Empty;
        if (content.Length == 0 || content.Contains((byte)0))
        {
            failure = "The DAV resource is empty or contains a NUL octet.";
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            failure = "DAV resources must use valid UTF-8.";
            return false;
        }
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var mediaType = NormalizeMediaType(requestContentType);
        var expectedMediaType = kind == DavCollectionKind.Calendar
            ? "text/calendar"
            : "text/vcard";
        if (kind == DavCollectionKind.Calendar && mediaType != "text/calendar")
        {
            failure = "Calendar resources must use the text/calendar media type.";
            return false;
        }
        if (kind == DavCollectionKind.AddressBook
            && mediaType is not ("text/vcard" or "text/x-vcard"))
        {
            failure = "Address-book resources must use a vCard media type.";
            return false;
        }

        var lines = UnfoldLines(text);
        return kind == DavCollectionKind.Calendar
            ? TryValidateCalendar(lines, text, expectedMediaType, out info, out failure)
            : TryValidateVCard(lines, text, expectedMediaType, out info, out failure);
    }

    private static bool TryValidateCalendar(
        IReadOnlyList<string> lines,
        string text,
        string contentType,
        out DavContentInfo? info,
        out string failure)
    {
        info = null;
        failure = string.Empty;
        if (lines.Count < 4
            || !lines[0].Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)
            || !lines[^1].Equals("END:VCALENDAR", StringComparison.OrdinalIgnoreCase)
            || !HasPropertyValue(lines, "VERSION", "2.0"))
        {
            failure = "The resource is not a complete iCalendar 2.0 object.";
            return false;
        }

        var components = new HashSet<string>(StringComparer.Ordinal);
        var uids = new HashSet<string>(StringComparer.Ordinal);
        var currentComponent = string.Empty;
        var properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var component = line[6..].ToUpperInvariant();
                if (component is "VEVENT" or "VTODO" or "VJOURNAL" or "VFREEBUSY")
                {
                    currentComponent = component;
                    components.Add(component);
                }
                continue;
            }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)
                && line[4..].Equals(currentComponent, StringComparison.OrdinalIgnoreCase))
            {
                currentComponent = string.Empty;
                continue;
            }
            if (currentComponent.Length == 0 || !TryParseProperty(line, out var name, out var value))
                continue;

            AddProperty(properties, name, value);
            if (name.Equals("UID", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                uids.Add(value.Trim());
            }
        }

        if (components.Count == 0 || uids.Count != 1)
        {
            failure = "A calendar resource must contain one logical component UID.";
            return false;
        }

        info = new DavContentInfo(
            uids.Single(),
            contentType,
            components,
            text,
            ToReadOnlyProperties(properties));
        return true;
    }

    private static bool TryValidateVCard(
        IReadOnlyList<string> lines,
        string text,
        string contentType,
        out DavContentInfo? info,
        out string failure)
    {
        info = null;
        failure = string.Empty;
        if (lines.Count < 4
            || !lines[0].Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)
            || !lines[^1].Equals("END:VCARD", StringComparison.OrdinalIgnoreCase)
            || !(HasPropertyValue(lines, "VERSION", "3.0")
                || HasPropertyValue(lines, "VERSION", "4.0")))
        {
            failure = "The resource is not a complete vCard 3.0 or 4.0 object.";
            return false;
        }

        var properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!TryParseProperty(line, out var name, out var value))
                continue;
            AddProperty(properties, name, value);
        }
        if (!properties.TryGetValue("UID", out var uidValues)
            || uidValues.Select(value => value.Trim()).Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray() is not [var uid])
        {
            failure = "A vCard resource must contain exactly one UID.";
            return false;
        }

        info = new DavContentInfo(
            uid,
            contentType,
            new HashSet<string>(StringComparer.Ordinal),
            text,
            ToReadOnlyProperties(properties));
        return true;
    }

    internal static IReadOnlyList<string> UnfoldLines(string text)
    {
        var physicalLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var result = new List<string>(physicalLines.Length);
        foreach (var physicalLine in physicalLines)
        {
            if (physicalLine.Length > 0
                && physicalLine[0] is ' ' or '\t'
                && result.Count > 0)
            {
                result[^1] += physicalLine[1..];
            }
            else if (physicalLine.Length > 0 || result.Count > 0)
            {
                result.Add(physicalLine);
            }
        }
        while (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    internal static bool TryParseProperty(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var separator = FindUnescapedColon(line);
        if (separator <= 0)
            return false;

        var nameEnd = line.IndexOf(';');
        if (nameEnd < 0 || nameEnd > separator)
            nameEnd = separator;
        name = line[..nameEnd];
        var groupSeparator = name.LastIndexOf('.');
        if (groupSeparator >= 0)
            name = name[(groupSeparator + 1)..];
        value = line[(separator + 1)..];
        return name.Length > 0;
    }

    private static int FindUnescapedColon(string line)
    {
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (!escaped && line[index] == ':')
                return index;
            escaped = !escaped && line[index] == '\\';
            if (line[index] != '\\')
                escaped = false;
        }
        return -1;
    }

    private static bool HasPropertyValue(
        IReadOnlyList<string> lines,
        string expectedName,
        string expectedValue) => lines.Any(line =>
        TryParseProperty(line, out var name, out var value)
        && name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
        && value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase));

    private static void AddProperty(
        Dictionary<string, List<string>> properties,
        string name,
        string value)
    {
        if (!properties.TryGetValue(name, out var values))
        {
            values = [];
            properties.Add(name, values);
        }
        values.Add(value);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToReadOnlyProperties(
        Dictionary<string, List<string>> properties) => properties.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);

    private static string NormalizeMediaType(string? contentType)
    {
        var separator = contentType?.IndexOf(';') ?? -1;
        return (separator >= 0 ? contentType![..separator] : contentType ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
    }
}
