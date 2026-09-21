using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal static class JmapContactCodec
{
    private const string JsonProperty = "X-MK8-JSCONTACT";
    private const string HashProperty = "X-MK8-JSCONTACT-HASH";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> MappedProperties = new HashSet<string>(
        [
            "uid", "kind", "name", "nicknames", "organizations", "titles", "emails",
            "phones", "onlineServices", "addresses", "links", "media", "members",
            "anniversaries", "keywords", "notes", "prodId", "created", "updated",
        ],
        StringComparer.Ordinal);

    public static bool TryValidate(JsonObject card, out IReadOnlyList<string> invalidProperties)
        => JmapContactValidator.TryValidate(card, out invalidProperties);

    public static JsonObject Decode(DavResourceDB resource)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(resource.Content);
        }
        catch (DecoderFallbackException)
        {
            return MinimalCard(resource);
        }

        var lines = Unfold(text);
        var embedded = DecodeEmbedded(lines);
        var storedHash = PropertyValue(lines, HashProperty);
        var coreLines = lines.Where(line => !IsProperty(line, JsonProperty)
            && !IsProperty(line, HashProperty)).ToArray();
        var actualHash = HashCore(coreLines);
        if (embedded is not null
            && string.Equals(storedHash, actualHash, StringComparison.OrdinalIgnoreCase)
            && TryValidate(embedded, out _))
        {
            return embedded;
        }

        var card = embedded is null
            ? new JsonObject()
            : (JsonObject)embedded.DeepClone();
        foreach (var property in MappedProperties)
            card.Remove(property);
        card["@type"] = "Card";
        card["version"] = "1.0";
        PopulateFromVCard(card, coreLines, resource);
        if (!TryString(card, "uid", out var uid) || string.IsNullOrWhiteSpace(uid))
            card["uid"] = resource.Uid;
        return card;
    }

    public static byte[] Encode(JsonObject value)
    {
        var card = (JsonObject)value.DeepClone();
        card.Remove("id");
        card.Remove("addressBookIds");

        var uid = StringValue(card["uid"]) ?? Guid.CreateVersion7().ToString("N");
        var core = new List<string>
        {
            "BEGIN:VCARD",
            "VERSION:4.0",
            "PRODID:" + EscapeText(StringValue(card["prodId"]) ?? "-//mk8.email//JMAP Contacts 1.0//EN"),
            "UID:" + EscapeText(uid),
        };

        var kind = StringValue(card["kind"]) ?? "individual";
        core.Add("KIND:" + EscapeText(kind));
        var fullName = FullName(card) ?? uid;
        core.Add("FN:" + EscapeText(fullName));
        AddStructuredName(core, card["name"] as JsonObject);
        AddSimpleMap(core, card["nicknames"] as JsonObject, "NICKNAME", "name");
        AddOrganizations(core, card["organizations"] as JsonObject);
        AddTitles(core, card["titles"] as JsonObject);
        AddEmails(core, card["emails"] as JsonObject);
        AddPhones(core, card["phones"] as JsonObject);
        AddResources(core, card["onlineServices"] as JsonObject, "IMPP");
        AddAddresses(core, card["addresses"] as JsonObject);
        AddResources(core, card["links"] as JsonObject, "URL");
        AddMedia(core, card["media"] as JsonObject);
        AddSimpleMap(core, card["notes"] as JsonObject, "NOTE", "note");
        AddAnniversaries(core, card["anniversaries"] as JsonObject);
        AddKeywords(core, card["keywords"] as JsonObject);
        if (card["members"] is JsonObject members)
        {
            foreach (var member in members.Where(item => item.Value?.GetValue<bool>() == true))
                core.Add("MEMBER:" + EscapeText(member.Key));
        }
        if (StringValue(card["created"]) is { } created)
            core.Add("CREATED:" + EscapeText(created));
        if (StringValue(card["updated"]) is { } updated)
            core.Add("REV:" + EscapeText(updated));
        core.Add("END:VCARD");

        var canonicalJson = card.ToJsonString(JmapJson.SerializerOptions);
        var encodedJson = Base64UrlEncode(Encoding.UTF8.GetBytes(canonicalJson));
        var hash = HashCore(core);
        core.Insert(core.Count - 1, HashProperty + ":" + hash);
        core.Insert(core.Count - 1, JsonProperty + ":" + encodedJson);

        var output = new StringBuilder();
        foreach (var line in core)
        {
            foreach (var folded in Fold(line))
                output.Append(folded).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static JsonObject MinimalCard(DavResourceDB resource) => new()
    {
        ["@type"] = "Card",
        ["version"] = "1.0",
        ["uid"] = resource.Uid,
        ["created"] = JmapDate.FormatUtc(resource.CreatedAt),
        ["updated"] = JmapDate.FormatUtc(resource.UpdatedAt),
    };

    private static void PopulateFromVCard(
        JsonObject card,
        IReadOnlyList<string> lines,
        DavResourceDB resource)
    {
        var emails = new JsonObject();
        var phones = new JsonObject();
        var onlineServices = new JsonObject();
        var addresses = new JsonObject();
        var organizations = new JsonObject();
        var titles = new JsonObject();
        var nicknames = new JsonObject();
        var links = new JsonObject();
        var media = new JsonObject();
        var notes = new JsonObject();
        var members = new JsonObject();
        var anniversaries = new JsonObject();
        var keywords = new JsonObject();
        JsonObject? name = null;
        var index = 0;

        foreach (var line in lines)
        {
            if (!TryParseProperty(line, out var property))
                continue;
            var value = UnescapeText(property.Value);
            switch (property.Name)
            {
                case "UID":
                    card["uid"] = value;
                    break;
                case "KIND":
                    card["kind"] = value.ToLowerInvariant();
                    break;
                case "PRODID":
                    card["prodId"] = value;
                    break;
                case "FN":
                    name ??= new JsonObject();
                    name["full"] = value;
                    break;
                case "N":
                    name ??= new JsonObject();
                    var components = SplitEscaped(property.Value, ';');
                    var nameComponents = new JsonArray();
                    AddNameComponent(nameComponents, "surname", components.ElementAtOrDefault(0));
                    AddNameComponent(nameComponents, "given", components.ElementAtOrDefault(1));
                    AddNameComponent(nameComponents, "given2", components.ElementAtOrDefault(2));
                    AddNameComponent(nameComponents, "prefix", components.ElementAtOrDefault(3));
                    AddNameComponent(nameComponents, "suffix", components.ElementAtOrDefault(4));
                    if (nameComponents.Count > 0)
                    {
                        name["components"] = nameComponents;
                        name["isOrdered"] = false;
                    }
                    break;
                case "NICKNAME":
                    foreach (var nickname in SplitEscaped(property.Value, ','))
                        nicknames[$"n{index++}"] = new JsonObject { ["name"] = UnescapeText(nickname) };
                    break;
                case "ORG":
                    var organizationParts = SplitEscaped(property.Value, ';');
                    var organization = new JsonObject
                    {
                        ["name"] = UnescapeText(organizationParts.ElementAtOrDefault(0) ?? string.Empty),
                    };
                    var units = organizationParts.Skip(1)
                        .Select(UnescapeText)
                        .Where(item => item.Length > 0)
                        .ToArray();
                    if (units.Length > 0)
                        organization["units"] = JmapMethodHelpers.ToJsonArray(units);
                    organizations[$"o{index++}"] = organization;
                    break;
                case "TITLE":
                case "ROLE":
                    titles[$"t{index++}"] = new JsonObject
                    {
                        ["name"] = value,
                        ["kind"] = property.Name == "ROLE" ? "role" : "title",
                    };
                    break;
                case "EMAIL":
                    emails[$"e{index++}"] = ContactValue(property, "address", value);
                    break;
                case "TEL":
                    phones[$"p{index++}"] = ContactValue(property, "number", value);
                    break;
                case "IMPP":
                    onlineServices[$"i{index++}"] = ResourceValue(property, value, null);
                    break;
                case "ADR":
                    addresses[$"d{index++}"] = AddressValue(property);
                    break;
                case "URL":
                    links[$"l{index++}"] = ResourceValue(property, value, null);
                    break;
                case "PHOTO":
                case "LOGO":
                case "SOUND":
                    media[$"m{index++}"] = ResourceValue(
                        property,
                        value,
                        property.Name.ToLowerInvariant());
                    break;
                case "NOTE":
                    notes[$"x{index++}"] = new JsonObject { ["note"] = value };
                    break;
                case "MEMBER":
                    members[value] = true;
                    break;
                case "BDAY":
                case "ANNIVERSARY":
                    if (AnniversaryValue(property.Name, value) is { } anniversary)
                        anniversaries[$"a{index++}"] = anniversary;
                    break;
                case "CATEGORIES":
                    foreach (var keyword in SplitEscaped(property.Value, ','))
                    {
                        var decoded = UnescapeText(keyword);
                        if (decoded.Length > 0)
                            keywords[decoded] = true;
                    }
                    break;
                case "CREATED":
                    card["created"] = value;
                    break;
                case "REV":
                    card["updated"] = value;
                    break;
            }
        }

        if (name is not null) card["name"] = name;
        AddIfNotEmpty(card, "emails", emails);
        AddIfNotEmpty(card, "phones", phones);
        AddIfNotEmpty(card, "onlineServices", onlineServices);
        AddIfNotEmpty(card, "addresses", addresses);
        AddIfNotEmpty(card, "organizations", organizations);
        AddIfNotEmpty(card, "titles", titles);
        AddIfNotEmpty(card, "nicknames", nicknames);
        AddIfNotEmpty(card, "links", links);
        AddIfNotEmpty(card, "media", media);
        AddIfNotEmpty(card, "notes", notes);
        AddIfNotEmpty(card, "members", members);
        AddIfNotEmpty(card, "anniversaries", anniversaries);
        AddIfNotEmpty(card, "keywords", keywords);
        card["created"] ??= JmapDate.FormatUtc(resource.CreatedAt);
        card["updated"] ??= JmapDate.FormatUtc(resource.UpdatedAt);
    }

    private static JsonObject ContactValue(VCardProperty property, string name, string value)
    {
        var result = new JsonObject { [name] = value };
        var contexts = Contexts(property);
        if (contexts.Count > 0)
            result["contexts"] = contexts;
        if (property.Parameters.TryGetValue("PREF", out var pref)
            && int.TryParse(pref, out var preference)
            && preference > 0)
        {
            result["pref"] = preference;
        }
        if (property.Parameters.TryGetValue("LABEL", out var label))
            result["label"] = UnescapeText(label.Trim('"'));
        return result;
    }

    private static JsonObject ResourceValue(
        VCardProperty property,
        string value,
        string? kind)
    {
        var mediaType = property.Parameters.TryGetValue("MEDIATYPE", out var explicitMediaType)
            ? explicitMediaType.Trim('"')
            : LegacyMediaType(property);
        var uri = IsBase64(property)
            ? $"data:{mediaType ?? "application/octet-stream"};base64,{RemoveAsciiWhitespace(value)}"
            : value;
        var result = new JsonObject { ["uri"] = uri };
        if (kind is not null)
            result["kind"] = kind;
        if (mediaType is not null)
            result["mediaType"] = mediaType;
        var contexts = Contexts(property);
        if (contexts.Count > 0)
            result["contexts"] = contexts;
        return result;
    }

    private static JsonObject AddressValue(VCardProperty property)
    {
        var parts = SplitEscaped(property.Value, ';');
        var components = new JsonArray();
        AddAddressComponent(components, "postOfficeBox", parts.ElementAtOrDefault(0));
        AddAddressComponent(components, "apartment", parts.ElementAtOrDefault(1));
        AddAddressComponent(components, "name", parts.ElementAtOrDefault(2));
        AddAddressComponent(components, "locality", parts.ElementAtOrDefault(3));
        AddAddressComponent(components, "region", parts.ElementAtOrDefault(4));
        AddAddressComponent(components, "postcode", parts.ElementAtOrDefault(5));
        AddAddressComponent(components, "country", parts.ElementAtOrDefault(6));
        var result = new JsonObject();
        if (components.Count > 0)
        {
            result["components"] = components;
            result["isOrdered"] = false;
        }
        if (property.Parameters.TryGetValue("LABEL", out var label))
            result["full"] = UnescapeText(label.Trim('"'));
        var contexts = Contexts(property);
        if (contexts.Count > 0)
            result["contexts"] = contexts;
        if (property.Parameters.TryGetValue("PREF", out var pref)
            && int.TryParse(pref, out var preference)
            && preference is >= 1 and <= 100)
        {
            result["pref"] = preference;
        }
        return result;
    }

    private static JsonObject? AnniversaryValue(string propertyName, string value)
    {
        var anniversary = new JsonObject
        {
            ["kind"] = propertyName == "BDAY" ? "birth" : "wedding",
        };
        if (TryVCardTimestamp(value, out var timestamp))
        {
            anniversary["date"] = new JsonObject
            {
                ["@type"] = "Timestamp",
                ["utc"] = JmapDate.FormatUtc(timestamp.UtcDateTime),
            };
            return anniversary;
        }

        var trimmed = value.Trim();
        var partial = new JsonObject { ["@type"] = "PartialDate" };
        if (trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            if (TryPositiveInt(trimmed.AsSpan(3), out var day))
                partial["day"] = day;
        }
        else if (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            var monthAndDay = trimmed[2..].Replace("-", string.Empty, StringComparison.Ordinal);
            if (monthAndDay.Length == 4
                && TryPositiveInt(monthAndDay.AsSpan(0, 2), out var month)
                && TryPositiveInt(monthAndDay.AsSpan(2, 2), out var day))
            {
                partial["month"] = month;
                partial["day"] = day;
            }
        }
        else
        {
            var digits = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
            if (digits.Length is 4 or 6 or 8
                && TryPositiveInt(digits.AsSpan(0, 4), out var year))
            {
                partial["year"] = year;
                if (digits.Length >= 6
                    && TryPositiveInt(digits.AsSpan(4, 2), out var month))
                    partial["month"] = month;
                if (digits.Length == 8
                    && TryPositiveInt(digits.AsSpan(6, 2), out var day))
                    partial["day"] = day;
            }
        }
        if (partial.Count == 1
            || IntegerValue(partial["month"]) is < 1 or > 12
            || IntegerValue(partial["day"]) is < 1 or > 31
            || partial.ContainsKey("day") && !partial.ContainsKey("month"))
        {
            return null;
        }
        if (IntegerValue(partial["month"]) is { } parsedMonth
            && IntegerValue(partial["day"]) is { } parsedDay)
        {
            var parsedYear = IntegerValue(partial["year"]);
            var maximumDay = DateTime.DaysInMonth(parsedYear is > 0 and <= 9999 ? parsedYear.Value : 2000, parsedMonth);
            if (parsedDay > maximumDay)
                return null;
        }
        anniversary["date"] = partial;
        return anniversary;
    }

    private static bool TryPositiveInt(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
        && result > 0;

    private static bool IsBase64(VCardProperty property) =>
        property.Parameters.TryGetValue("ENCODING", out var encoding)
        && encoding.Trim('"') is var normalized
        && (normalized.Equals("b", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("base64", StringComparison.OrdinalIgnoreCase));

    private static string? LegacyMediaType(VCardProperty property)
    {
        if (!property.Parameters.TryGetValue("TYPE", out var values))
            return null;
        foreach (var value in values.Split(','))
        {
            var normalized = value.Trim().Trim('"').ToLowerInvariant();
            var mediaType = normalized switch
            {
                "jpeg" or "jpg" => "image/jpeg",
                "png" => "image/png",
                "gif" => "image/gif",
                "webp" => "image/webp",
                "wav" => "audio/wav",
                "mp3" => "audio/mpeg",
                "ogg" => "audio/ogg",
                _ => null,
            };
            if (mediaType is not null)
                return mediaType;
        }
        return null;
    }

    private static string RemoveAsciiWhitespace(string value) => new(
        value.Where(character => character is not (' ' or '\t' or '\r' or '\n')).ToArray());

    private static bool TryVCardTimestamp(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            value,
            [
                "yyyyMMdd'T'HHmmss'Z'",
                "yyyyMMdd'T'HHmmsszzz",
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:sszzz",
            ],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);

    private static void AddAddressComponent(JsonArray components, string kind, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        var decoded = UnescapeText(value);
        if (decoded.Length > 0)
            components.Add(new JsonObject { ["kind"] = kind, ["value"] = decoded });
    }

    private static JsonObject Contexts(VCardProperty property)
    {
        var contexts = new JsonObject();
        if (!property.Parameters.TryGetValue("TYPE", out var types))
            return contexts;
        foreach (var type in types.Split(','))
        {
            var normalized = type.Trim().Trim('"').ToLowerInvariant() switch
            {
                "home" => "private",
                "work" => "work",
                _ => null,
            };
            if (normalized is not null)
                contexts[normalized] = true;
        }
        return contexts;
    }

    private static void AddStructuredName(List<string> lines, JsonObject? name)
    {
        if (name?["components"] is not JsonArray components)
            return;
        string Values(params string[] kinds) => string.Join(" ", components
            .OfType<JsonObject>()
            .Where(component => kinds.Contains(StringValue(component["kind"]), StringComparer.Ordinal))
            .Select(component => StringValue(component["value"]))
            .Where(value => !string.IsNullOrEmpty(value)));
        var family = Values("surname", "surname2");
        var given = Values("given");
        var additional = Values("given2");
        var prefix = Values("prefix");
        var suffix = Values("suffix");
        lines.Add("N:" + string.Join(';', new[] { family, given, additional, prefix, suffix }
            .Select(EscapeText)));
    }

    private static void AddOrganizations(List<string> lines, JsonObject? values)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var name = StringValue(value["name"]);
            if (string.IsNullOrEmpty(name))
                continue;
            var parts = new List<string> { EscapeText(name) };
            if (value["units"] is JsonArray units)
            {
                parts.AddRange(units.Select(unit => EscapeText(StringValue(unit) ?? string.Empty)));
            }
            lines.Add("ORG:" + string.Join(';', parts));
        }
    }

    private static void AddTitles(List<string> lines, JsonObject? values)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var name = StringValue(value["name"]);
            if (string.IsNullOrEmpty(name))
                continue;
            lines.Add((StringValue(value["kind"]) == "role" ? "ROLE:" : "TITLE:")
                + EscapeText(name));
        }
    }

    private static void AddEmails(List<string> lines, JsonObject? values) =>
        AddContactValues(lines, values, "EMAIL", "address");

    private static void AddPhones(List<string> lines, JsonObject? values) =>
        AddContactValues(lines, values, "TEL", "number");

    private static void AddContactValues(
        List<string> lines,
        JsonObject? values,
        string property,
        string valueProperty)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var text = StringValue(value[valueProperty]);
            if (string.IsNullOrEmpty(text))
                continue;
            var parameters = Parameters(value);
            lines.Add(property + parameters + ":" + EscapeText(text));
        }
    }

    private static string Parameters(JsonObject value)
    {
        var result = new StringBuilder();
        if (value["contexts"] is JsonObject contexts)
        {
            var types = contexts.Where(item => item.Value?.GetValue<bool>() == true)
                .Select(item => item.Key switch
                {
                    "private" => "HOME",
                    "work" => "WORK",
                    _ => item.Key.ToUpperInvariant(),
                })
                .ToArray();
            if (types.Length > 0)
                result.Append(";TYPE=").Append(string.Join(',', types));
        }
        if (value["pref"] is JsonValue prefValue
            && prefValue.TryGetValue<int>(out var pref)
            && pref > 0)
        {
            result.Append(";PREF=").Append(pref);
        }
        if (StringValue(value["label"]) is { Length: > 0 } label)
            result.Append(";LABEL=\"").Append(EscapeParameter(label)).Append('"');
        return result.ToString();
    }

    private static void AddAddresses(List<string> lines, JsonObject? values)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var fields = new string[7];
            if (value["components"] is JsonArray components)
            {
                fields[0] = AddressComponents(components, "postOfficeBox");
                fields[1] = AddressComponents(components, "apartment", "room", "floor", "building");
                fields[2] = AddressComponents(
                    components,
                    "number",
                    "name",
                    "block",
                    "direction",
                    "landmark");
                fields[3] = AddressComponents(components, "locality", "district", "subdistrict");
                fields[4] = AddressComponents(components, "region");
                fields[5] = AddressComponents(components, "postcode");
                fields[6] = AddressComponents(components, "country");
            }
            var label = StringValue(value["full"]);
            if (fields.All(string.IsNullOrEmpty) && string.IsNullOrEmpty(label))
                continue;
            lines.Add("ADR" + Parameters(value)
                + (string.IsNullOrEmpty(label) ? string.Empty : ";LABEL=\"" + EscapeParameter(label) + '"')
                + ":" + string.Join(';', fields.Select(EscapeText)));
        }
    }

    private static void AddResources(
        List<string> lines,
        JsonObject? values,
        string property)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var uri = StringValue(value["uri"]);
            if (!string.IsNullOrEmpty(uri))
                lines.Add(property + Parameters(value) + ":" + EscapeText(uri));
        }
    }

    private static void AddMedia(List<string> lines, JsonObject? values)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var uri = StringValue(value["uri"]);
            if (string.IsNullOrEmpty(uri))
                continue;
            var property = StringValue(value["kind"]) switch
            {
                "logo" => "LOGO",
                "sound" => "SOUND",
                _ => "PHOTO",
            };
            var mediaType = StringValue(value["mediaType"]);
            lines.Add(property
                + (string.IsNullOrEmpty(mediaType) ? string.Empty : ";MEDIATYPE=" + mediaType)
                + Parameters(value)
                + ":" + uri);
        }
    }

    private static void AddAnniversaries(List<string> lines, JsonObject? values)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var date = value["date"] is JsonObject dateObject
                ? FormatAnniversaryDate(dateObject)
                : null;
            if (string.IsNullOrEmpty(date))
                continue;
            lines.Add((StringValue(value["kind"]) == "birth" ? "BDAY:" : "ANNIVERSARY:")
                + EscapeText(date));
        }
    }

    private static void AddKeywords(List<string> lines, JsonObject? values)
    {
        if (values is null)
            return;
        var keywords = values
            .Where(item => item.Value is JsonValue json
                && json.TryGetValue<bool>(out var included)
                && included)
            .Select(item => EscapeText(item.Key))
            .ToArray();
        if (keywords.Length > 0)
            lines.Add("CATEGORIES:" + string.Join(',', keywords));
    }

    private static string AddressComponents(JsonArray components, params string[] kinds) =>
        string.Join(' ', components.OfType<JsonObject>()
            .Where(component => kinds.Contains(StringValue(component["kind"]), StringComparer.Ordinal))
            .Select(component => StringValue(component["value"]))
            .Where(value => !string.IsNullOrEmpty(value))!);

    private static string? FormatAnniversaryDate(JsonObject value)
    {
        if (StringValue(value["@type"]) == "Timestamp")
        {
            return StringValue(value["utc"]) is { } utc
                && JmapDate.TryParseUtcDate(utc, out var timestamp)
                    ? timestamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                    : null;
        }
        var year = IntegerValue(value["year"]);
        var month = IntegerValue(value["month"]);
        var day = IntegerValue(value["day"]);
        if (year is not null && month is not null && day is not null)
            return $"{year:0000}{month:00}{day:00}";
        if (year is not null && month is not null)
            return $"{year:0000}{month:00}";
        if (year is not null)
            return $"{year:0000}";
        if (month is not null && day is not null)
            return $"--{month:00}{day:00}";
        if (day is not null)
            return $"---{day:00}";
        return null;
    }

    private static void AddSimpleMap(
        List<string> lines,
        JsonObject? values,
        string property,
        string valueProperty)
    {
        if (values is null)
            return;
        foreach (var value in values.Select(item => item.Value).OfType<JsonObject>())
        {
            var text = StringValue(value[valueProperty]);
            if (!string.IsNullOrEmpty(text))
                lines.Add(property + ":" + EscapeText(text));
        }
    }

    private static string? FullName(JsonObject card)
    {
        if (card["name"] is JsonObject name)
        {
            if (StringValue(name["full"]) is { Length: > 0 } full)
                return full;
            if (name["components"] is JsonArray components)
            {
                var values = components.OfType<JsonObject>()
                    .Where(component => StringValue(component["kind"]) != "separator")
                    .Select(component => StringValue(component["value"]))
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                var joined = string.Join(' ', values!);
                if (joined.Length > 0)
                    return joined;
            }
        }
        if (card["organizations"] is JsonObject organizations)
        {
            return organizations.Select(item => item.Value)
                .OfType<JsonObject>()
                .Select(value => StringValue(value["name"]))
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        }
        return null;
    }

    private static JsonObject? DecodeEmbedded(IReadOnlyList<string> lines)
    {
        var encoded = PropertyValue(lines, JsonProperty);
        if (encoded is null || !TryBase64UrlDecode(encoded, out var bytes))
            return null;
        try
        {
            return JsonNode.Parse(StrictUtf8.GetString(bytes)) as JsonObject;
        }
        catch (Exception exception) when (exception is FormatException
            or System.Text.Json.JsonException
            or DecoderFallbackException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Unfold(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n').Split('\n'))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t' && result.Count > 0)
                result[^1] += line[1..];
            else if (line.Length > 0)
                result.Add(line);
        }
        return result;
    }

    private static IReadOnlyList<string> Fold(string line)
    {
        var result = new List<string>();
        var remaining = line.AsSpan();
        var first = true;
        while (!remaining.IsEmpty)
        {
            var maximumBytes = first ? 75 : 74;
            var characterCount = 0;
            var byteCount = 0;
            foreach (var rune in remaining.EnumerateRunes())
            {
                if (byteCount + rune.Utf8SequenceLength > maximumBytes)
                    break;
                byteCount += rune.Utf8SequenceLength;
                characterCount += rune.Utf16SequenceLength;
            }
            if (characterCount == 0)
                characterCount = remaining.Length;
            result.Add((first ? string.Empty : " ") + remaining[..characterCount].ToString());
            remaining = remaining[characterCount..];
            first = false;
        }
        return result;
    }

    private static bool TryParseProperty(string line, out VCardProperty property)
    {
        property = default!;
        var colon = FindUnescaped(line, ':');
        if (colon <= 0)
            return false;
        var header = line[..colon];
        var parts = SplitHeader(header, ';');
        var name = parts[0];
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
                parameters[part[..equals].ToUpperInvariant()] = part[(equals + 1)..];
        }
        property = new VCardProperty(
            name.ToUpperInvariant(),
            parameters,
            line[(colon + 1)..]);
        return true;
    }

    private static string[] SplitEscaped(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!escaped && value[index] == separator)
            {
                result.Add(value[start..index]);
                start = index + 1;
            }
            escaped = !escaped && value[index] == '\\';
            if (value[index] != '\\')
                escaped = false;
        }
        result.Add(value[start..]);
        return result.ToArray();
    }

    private static int FindUnescaped(string value, char expected)
    {
        var escaped = false;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!escaped && value[index] == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!escaped && !quoted && value[index] == expected)
                return index;
            escaped = !escaped && value[index] == '\\';
            if (value[index] != '\\')
                escaped = false;
        }
        return -1;
    }

    private static string[] SplitHeader(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var escaped = false;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!escaped && value[index] == '"')
                quoted = !quoted;
            else if (!escaped && !quoted && value[index] == separator)
            {
                result.Add(value[start..index]);
                start = index + 1;
            }
            escaped = !escaped && value[index] == '\\';
            if (value[index] != '\\')
                escaped = false;
        }
        result.Add(value[start..]);
        return result.ToArray();
    }

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal);

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

    private static string EscapeParameter(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool IsProperty(string line, string name) =>
        line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith(name + ";", StringComparison.OrdinalIgnoreCase);

    private static string? PropertyValue(IReadOnlyList<string> lines, string name)
    {
        var line = lines.FirstOrDefault(candidate => IsProperty(candidate, name));
        if (line is null)
            return null;
        var colon = FindUnescaped(line, ':');
        return colon < 0 ? null : line[(colon + 1)..];
    }

    private static string HashCore(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")));

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? IntegerValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool TryString(JsonObject value, string name, out string text)
    {
        text = string.Empty;
        return value[name] is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out text!)
            && text is not null;
    }

    private static void AddNameComponent(JsonArray values, string kind, string? value)
    {
        var decoded = value is null ? null : UnescapeText(value);
        if (!string.IsNullOrEmpty(decoded))
            values.Add(new JsonObject { ["kind"] = kind, ["value"] = decoded });
    }

    private static void AddIfNotEmpty(JsonObject card, string name, JsonObject value)
    {
        if (value.Count > 0)
            card[name] = value;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            return false;
        }
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => "!",
        };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record VCardProperty(
        string Name,
        IReadOnlyDictionary<string, string> Parameters,
        string Value);
}
