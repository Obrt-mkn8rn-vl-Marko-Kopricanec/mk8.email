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
            || lines.Count(line => line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) != 1
            || lines.Count(line => line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase)) != 1)
        {
            failure = "The resource is not a complete vCard 3.0 or 4.0 object.";
            return false;
        }

        var parsed = new List<VCardProperty>(lines.Count);
        var properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!TryParseVCardProperty(line, out var property))
            {
                failure = "The vCard contains an invalid content line.";
                return false;
            }
            parsed.Add(property);
            AddProperty(properties, property.Name, property.Value);
        }

        var versions = parsed.Where(property => property.Name == "VERSION").ToArray();
        if (versions.Length != 1 || versions[0].Value is not ("3.0" or "4.0"))
        {
            failure = "The resource must contain exactly one supported vCard VERSION.";
            return false;
        }
        var version = versions[0].Value;

        var uidProperties = parsed.Where(property => property.Name == "UID").ToArray();
        if (uidProperties.Length != 1
            || !TryVCardUid(uidProperties[0], version, out var uid)
            || string.IsNullOrWhiteSpace(uid)
            || StrictUtf8.GetByteCount(uid) > 255)
        {
            failure = "A vCard resource must contain exactly one valid UID with matching VALUE semantics.";
            return false;
        }

        if (!parsed.Any(property => property.Name == "FN"))
        {
            failure = "A vCard resource must contain an FN property.";
            return false;
        }

        var kinds = parsed.Where(property => property.Name == "KIND").ToArray();
        if (kinds.Length > 1)
        {
            failure = "A vCard resource must not contain multiple KIND properties.";
            return false;
        }
        var members = parsed.Where(property => property.Name == "MEMBER").ToArray();
        var isGroup = kinds.Length == 1
            && UnescapeText(kinds[0].Value).Equals("group", StringComparison.OrdinalIgnoreCase);
        if (members.Length > 0 && !isGroup)
        {
            failure = "MEMBER properties are only valid on group vCards.";
            return false;
        }
        if (members.Any(member => !HasUriValueType(member) || !IsAbsoluteUri(member.Value)))
        {
            failure = "Every MEMBER property must contain a single absolute URI value.";
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

    private static bool TryVCardUid(VCardProperty property, string version, out string uid)
    {
        uid = string.Empty;
        var valueType = ParameterValue(property, "VALUE");
        if (valueType is not null
            && valueType is not ("text" or "uri"))
        {
            return false;
        }

        if (valueType == "uri" || version == "4.0" && valueType is null)
        {
            if (!IsAbsoluteUri(property.Value))
                return false;
            uid = property.Value;
            return true;
        }

        uid = UnescapeText(property.Value);
        return uid.Length > 0;
    }

    private static bool HasUriValueType(VCardProperty property)
    {
        var valueType = ParameterValue(property, "VALUE");
        return valueType is null or "uri";
    }

    private static string? ParameterValue(VCardProperty property, string name) =>
        property.Parameters.TryGetValue(name, out var value)
            ? value.Trim().Trim('"').ToLowerInvariant()
            : null;

    private static bool IsAbsoluteUri(string value) =>
        value.Length > 0
        && !value.Any(character => char.IsWhiteSpace(character)
            || char.IsControl(character)
            || character == '\\')
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && !string.IsNullOrEmpty(uri.Scheme);

    private static string UnescapeText(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                result.Append(value[index]);
                continue;
            }

            var next = value[++index];
            result.Append(next is 'n' or 'N' ? '\n' : next);
        }
        return result.ToString();
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
        if (TryParseVCardProperty(line, out var property))
        {
            name = property.Name;
            value = property.Value;
            return true;
        }
        name = string.Empty;
        value = string.Empty;
        return false;
    }

    private static bool TryParseVCardProperty(string line, out VCardProperty property)
    {
        property = default!;
        var separator = FindUnescapedColon(line);
        if (separator <= 0)
            return false;

        var headerParts = SplitHeader(line[..separator]);
        var name = headerParts[0];
        var groupSeparator = name.LastIndexOf('.');
        if (groupSeparator >= 0)
            name = name[(groupSeparator + 1)..];
        if (name.Length == 0)
            return false;

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in headerParts.Skip(1))
        {
            var equals = parameter.IndexOf('=');
            if (equals <= 0 || equals == parameter.Length - 1)
                return false;
            parameters[parameter[..equals].ToUpperInvariant()] = parameter[(equals + 1)..];
        }

        property = new VCardProperty(
            name.ToUpperInvariant(),
            parameters,
            line[(separator + 1)..]);
        return true;
    }

    private static int FindUnescapedColon(string line)
    {
        var escaped = false;
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (!escaped && line[index] == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!escaped && !quoted && line[index] == ':')
                return index;
            escaped = !escaped && line[index] == '\\';
            if (line[index] != '\\')
                escaped = false;
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitHeader(string value)
    {
        var result = new List<string>();
        var start = 0;
        var escaped = false;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!escaped && value[index] == '"')
                quoted = !quoted;
            else if (!escaped && !quoted && value[index] == ';')
            {
                result.Add(value[start..index]);
                start = index + 1;
            }
            escaped = !escaped && value[index] == '\\';
            if (value[index] != '\\')
                escaped = false;
        }
        result.Add(value[start..]);
        return result;
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

    private sealed record VCardProperty(
        string Name,
        IReadOnlyDictionary<string, string> Parameters,
        string Value);
}
