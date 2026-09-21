using System.Text;
using System.Text.Json.Nodes;
using MimeKit;

namespace mk8.email.Jmap;

internal static class JmapContactValidator
{
    private static readonly IReadOnlySet<string> CardProperties = new HashSet<string>(
        [
            "@type", "version", "created", "kind", "language", "members", "prodId",
            "relatedTo", "uid", "updated", "name", "nicknames", "organizations",
            "speakToAs", "titles", "emails", "onlineServices", "phones",
            "preferredLanguages", "calendars", "schedulingAddresses", "addresses",
            "cryptoKeys", "directories", "links", "media", "localizations",
            "anniversaries", "keywords", "notes", "personalInfo",
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ContextRestrictedProperties = new HashSet<string>(
        ["id", "addressBookIds", "blobId", "extra"],
        StringComparer.Ordinal);

    public static bool TryValidate(JsonObject card, out IReadOnlyList<string> invalidProperties)
    {
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        ValidatePropertyNames(card, invalid);

        if (!HasString(card, "@type", out var type) || type != "Card")
            invalid.Add("@type");
        if (!HasString(card, "version", out var version) || version != "1.0")
            invalid.Add("version");
        if (!HasString(card, "uid", out var uid)
            || string.IsNullOrWhiteSpace(uid)
            || Encoding.UTF8.GetByteCount(uid) > 255)
        {
            invalid.Add("uid");
        }

        ValidateOptionalString(card, "language", invalid);
        if (card.TryGetPropertyValue("prodId", out var prodId)
            && (!TryString(prodId, out var product) || product.Length == 0))
        {
            invalid.Add("prodId");
        }
        if (card.TryGetPropertyValue("kind", out var kind)
            && (!TryString(kind, out var kindText) || kindText.Length == 0))
        {
            invalid.Add("kind");
        }

        ValidateUtcDate(card, "created", invalid);
        ValidateUtcDate(card, "updated", invalid);
        ValidateTrueSet(card, "members", invalid);
        if (card.ContainsKey("members")
            && (!HasString(card, "kind", out var memberKind) || memberKind != "group"))
        {
            invalid.Add("members");
        }
        ValidateTrueSet(card, "keywords", invalid);

        ValidateObject(card, "name", ValidateName, invalid);
        ValidateObject(card, "speakToAs", ValidateSpeakToAs, invalid);
        ValidateStringObjectMap(card, "relatedTo", ValidateRelation, invalid);
        ValidateIdObjectMap(card, "nicknames", ValidateNickname, invalid);
        ValidateIdObjectMap(card, "organizations", ValidateOrganization, invalid);
        ValidateIdObjectMap(card, "titles", ValidateTitle, invalid);
        ValidateIdObjectMap(card, "emails", ValidateEmail, invalid);
        ValidateIdObjectMap(card, "onlineServices", ValidateOnlineService, invalid);
        ValidateIdObjectMap(card, "phones", ValidatePhone, invalid);
        ValidateIdObjectMap(card, "preferredLanguages", ValidateLanguagePreference, invalid);
        ValidateIdObjectMap(
            card,
            "calendars",
            value => ValidateResource(value, "Calendar", requireKind: true),
            invalid);
        ValidateIdObjectMap(
            card,
            "schedulingAddresses",
            value => ValidateResource(value, "SchedulingAddress"),
            invalid);
        ValidateIdObjectMap(card, "addresses", ValidateAddress, invalid);
        ValidateIdObjectMap(card, "cryptoKeys", value => ValidateResource(value, "CryptoKey"), invalid);
        ValidateIdObjectMap(
            card,
            "directories",
            value => ValidateResource(value, "Directory", requireKind: true)
                && (!value.TryGetPropertyValue("listAs", out var listAs)
                    || TryUnsigned(listAs, out var position) && position > 0),
            invalid);
        ValidateIdObjectMap(card, "links", value => ValidateResource(value, "Link"), invalid);
        ValidateIdObjectMap(card, "media", ValidateMedia, invalid);
        ValidateIdObjectMap(card, "anniversaries", ValidateAnniversary, invalid);
        ValidateIdObjectMap(card, "notes", ValidateNote, invalid);
        ValidateIdObjectMap(card, "personalInfo", ValidatePersonalInfo, invalid);
        ValidateLocalizations(card, invalid);

        invalidProperties = invalid.Order(StringComparer.Ordinal).ToArray();
        return invalid.Count == 0;
    }

    public static bool IsSupportedCardProperty(string property) =>
        property is "id" or "addressBookIds"
        || CardProperties.Contains(property)
        || !ContextRestrictedProperties.Contains(property)
            && !CardProperties.Any(known => string.Equals(
                known,
                property,
                StringComparison.OrdinalIgnoreCase))
            && !ContextRestrictedProperties.Any(known => string.Equals(
                known,
                property,
                StringComparison.OrdinalIgnoreCase))
            && IsValidExtensionProperty(property);

    private static void ValidatePropertyNames(JsonObject card, ISet<string> invalid)
    {
        foreach (var property in card.Select(item => item.Key))
        {
            if (CardProperties.Contains(property))
                continue;
            if (ContextRestrictedProperties.Contains(property)
                || CardProperties.Any(known => string.Equals(
                    known,
                    property,
                    StringComparison.OrdinalIgnoreCase))
                || ContextRestrictedProperties.Any(known => string.Equals(
                    known,
                    property,
                    StringComparison.OrdinalIgnoreCase))
                || !IsValidExtensionProperty(property))
            {
                invalid.Add(property);
            }
        }
    }

    private static bool IsValidExtensionProperty(string value)
    {
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            if (colon == 0 || colon == value.Length - 1 || value.IndexOf(':', colon + 1) >= 0)
                return false;
            var labels = value[..colon].Split('.');
            if (labels.Any(label => label.Length == 0
                    || !IsAlphaNumeric(label[0])
                    || !IsAlphaNumeric(label[^1])
                    || label.Any(character => !IsAlphaNumeric(character) && character != '-')))
            {
                return false;
            }
            return value[(colon + 1)..].All(character =>
                character is not ('/' or '~' or '"') && !char.IsControl(character));
        }

        return value.Length > 0
            && value[0] is >= 'a' and <= 'z'
            && value.All(char.IsAsciiLetterOrDigit);
    }

    private static bool IsAlphaNumeric(char value) =>
        char.IsAsciiLetterOrDigit(value) || value > 0x7f;

    private static bool ValidateName(JsonObject value)
    {
        if (!HasExpectedType(value, "Name")
            || value.TryGetPropertyValue("full", out var full) && !TryString(full, out _)
            || value.TryGetPropertyValue("isOrdered", out var ordered) && !TryBoolean(ordered, out _)
            || value.TryGetPropertyValue("defaultSeparator", out var separator)
                && !TryString(separator, out _)
            || value.TryGetPropertyValue("sortAs", out var sortAs) && !IsStringMap(sortAs))
        {
            return false;
        }

        var hasFull = value["full"] is JsonValue;
        if (!value.TryGetPropertyValue("components", out var componentsNode))
            return hasFull;
        if (componentsNode is not JsonArray components || components.Count == 0)
            return false;
        var isOrdered = value["isOrdered"]?.GetValue<bool>() ?? false;
        var hasNonSeparator = false;
        foreach (var componentNode in components)
        {
            if (componentNode is not JsonObject component
                || !HasExpectedType(component, "NameComponent")
                || !HasString(component, "kind", out var kind)
                || !HasString(component, "value", out _)
                || component.TryGetPropertyValue("phonetic", out var phonetic)
                    && !TryString(phonetic, out _))
            {
                return false;
            }
            if (kind == "separator" && !isOrdered)
                return false;
            hasNonSeparator |= kind != "separator";
        }
        return hasNonSeparator && (isOrdered || !value.ContainsKey("defaultSeparator"));
    }

    private static bool ValidateSpeakToAs(JsonObject value)
    {
        if (!HasExpectedType(value, "SpeakToAs"))
            return false;
        var hasGender = value.TryGetPropertyValue("grammaticalGender", out var gender);
        if (hasGender && !TryString(gender, out _))
            return false;
        var hasPronouns = value.TryGetPropertyValue("pronouns", out var pronounsNode);
        if (hasPronouns && !ValidateIdMapNode(pronounsNode, pronoun =>
                HasExpectedType(pronoun, "Pronouns")
                && HasString(pronoun, "pronouns", out _)
                && ValidateCommon(pronoun)))
        {
            return false;
        }
        return hasGender || hasPronouns;
    }

    private static bool ValidateRelation(JsonObject value) =>
        HasExpectedType(value, "Relation")
        && (!value.TryGetPropertyValue("relation", out var relation) || IsTrueSet(relation));

    private static bool ValidateNickname(JsonObject value) =>
        HasExpectedType(value, "Nickname")
        && HasString(value, "name", out _)
        && ValidateCommon(value);

    private static bool ValidateOrganization(JsonObject value)
    {
        if (!HasExpectedType(value, "Organization")
            || value.TryGetPropertyValue("name", out var name) && !TryString(name, out _)
            || value.TryGetPropertyValue("sortAs", out var sortAs) && !TryString(sortAs, out _)
            || !ValidateCommon(value))
        {
            return false;
        }
        var hasName = value["name"] is JsonValue;
        if (!value.TryGetPropertyValue("units", out var unitsNode))
            return hasName;
        return unitsNode is JsonArray { Count: > 0 } units
            && units.All(unit => unit is JsonObject objectValue
                && HasExpectedType(objectValue, "OrgUnit")
                && HasString(objectValue, "name", out _)
                && (!objectValue.TryGetPropertyValue("sortAs", out var unitSort)
                    || TryString(unitSort, out _)));
    }

    private static bool ValidateTitle(JsonObject value) =>
        HasExpectedType(value, "Title")
        && HasString(value, "name", out _)
        && (!value.TryGetPropertyValue("kind", out var kind) || TryString(kind, out _))
        && (!value.TryGetPropertyValue("organizationId", out var organizationId)
            || TryString(organizationId, out var id) && JmapId.IsValidId(id));

    private static bool ValidateEmail(JsonObject value)
    {
        if (!HasExpectedType(value, "EmailAddress")
            || !HasString(value, "address", out var address)
            || !MailboxAddress.TryParse(address, out var mailbox)
            || !string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return ValidateCommon(value);
    }

    private static bool ValidateOnlineService(JsonObject value)
    {
        if (!HasExpectedType(value, "OnlineService") || !ValidateCommon(value))
            return false;
        var hasUri = value.TryGetPropertyValue("uri", out var uriNode);
        var hasUser = value.TryGetPropertyValue("user", out var userNode);
        return (!value.TryGetPropertyValue("service", out var service) || TryString(service, out _))
            && (!hasUri || TryAbsoluteUri(uriNode, out _))
            && (!hasUser || TryString(userNode, out _))
            && (hasUri || hasUser);
    }

    private static bool ValidatePhone(JsonObject value) =>
        HasExpectedType(value, "Phone")
        && HasString(value, "number", out _)
        && (!value.TryGetPropertyValue("features", out var features) || IsTrueSet(features))
        && ValidateCommon(value);

    private static bool ValidateLanguagePreference(JsonObject value) =>
        HasExpectedType(value, "LanguagePreference")
        && HasString(value, "language", out _)
        && ValidateCommon(value);

    private static bool ValidateAddress(JsonObject value)
    {
        Uri? coordinateUri = null;
        if (!HasExpectedType(value, "Address")
            || !ValidateCommon(value)
            || value.TryGetPropertyValue("full", out var full) && !TryString(full, out _)
            || value.TryGetPropertyValue("coordinates", out var coordinates)
                && !TryAbsoluteUri(coordinates, out coordinateUri)
            || value.TryGetPropertyValue("countryCode", out var country)
                && (!TryString(country, out var countryText) || countryText.Length != 2)
            || value.TryGetPropertyValue("timeZone", out var zone) && !TryString(zone, out _)
            || value.TryGetPropertyValue("isOrdered", out var ordered) && !TryBoolean(ordered, out _)
            || value.TryGetPropertyValue("defaultSeparator", out var separator)
                && !TryString(separator, out _))
        {
            return false;
        }
        if (coordinateUri is not null && coordinateUri.Scheme != "geo")
            return false;

        var hasValue = value["full"] is JsonValue
            || value["coordinates"] is JsonValue
            || value["countryCode"] is JsonValue
            || value["timeZone"] is JsonValue;
        if (!value.TryGetPropertyValue("components", out var componentsNode))
            return hasValue;
        if (componentsNode is not JsonArray { Count: > 0 } components)
            return false;
        var isOrdered = value["isOrdered"]?.GetValue<bool>() ?? false;
        var hasNonSeparator = false;
        foreach (var componentNode in components)
        {
            if (componentNode is not JsonObject component
                || !HasExpectedType(component, "AddressComponent")
                || !HasString(component, "kind", out var kind)
                || !HasString(component, "value", out _))
            {
                return false;
            }
            if (kind == "separator" && !isOrdered)
                return false;
            hasNonSeparator |= kind != "separator";
        }
        return hasNonSeparator && (isOrdered || !value.ContainsKey("defaultSeparator"));
    }

    private static bool ValidateResource(
        JsonObject value,
        string expectedType,
        bool requireKind = false) =>
        HasExpectedType(value, expectedType)
        && TryAbsoluteUri(value["uri"], out _)
        && ValidateCommon(value)
        && (!requireKind || HasString(value, "kind", out var kind) && kind.Length > 0)
        && (requireKind || !value.TryGetPropertyValue("kind", out var optionalKind)
            || TryString(optionalKind, out _))
        && (!value.TryGetPropertyValue("mediaType", out var mediaType)
            || TryString(mediaType, out _));

    private static bool ValidateMedia(JsonObject value)
    {
        if (!HasExpectedType(value, "Media")
            || !ValidateCommon(value)
            || !HasString(value, "kind", out var kind)
            || kind.Length == 0
            || value.TryGetPropertyValue("mediaType", out var mediaType)
                && !TryString(mediaType, out _))
        {
            return false;
        }
        var hasUri = value.TryGetPropertyValue("uri", out var uri);
        var hasBlob = value.TryGetPropertyValue("blobId", out var blob);
        return (hasUri || hasBlob)
            && (!hasUri || TryAbsoluteUri(uri, out _))
            && (!hasBlob || TryString(blob, out var blobId) && JmapId.IsValidId(blobId));
    }

    private static bool ValidateAnniversary(JsonObject value)
    {
        if (!HasExpectedType(value, "Anniversary")
            || !HasString(value, "kind", out _)
            || value["date"] is not JsonObject date)
        {
            return false;
        }
        if (date.TryGetPropertyValue("@type", out var typeNode)
            && TryString(typeNode, out var dateType)
            && dateType == "Timestamp")
            return HasString(date, "utc", out var utc) && JmapDate.TryParseUtcDate(utc, out _);
        if (!HasExpectedType(date, "PartialDate"))
            return false;
        if (!TryOptionalUnsigned(date, "year", out var year)
            || !TryOptionalUnsigned(date, "month", out var month)
            || !TryOptionalUnsigned(date, "day", out var day)
            || date.TryGetPropertyValue("calendarScale", out var calendarScale)
                && !TryString(calendarScale, out _))
        {
            return false;
        }
        var hasYear = year is not null;
        var hasMonth = month is not null;
        var hasDay = day is not null;
        if ((!hasYear && !hasMonth && !hasDay)
            || month is < 1 or > 12
            || day is < 1 or > 31
            || hasDay && !hasMonth
            || hasMonth && !hasYear && !hasDay)
        {
            return false;
        }
        if (month is not null && day is not null)
        {
            var maximumDay = DateTime.DaysInMonth(
                year is > 0 and <= 9999 ? checked((int)year.Value) : 2000,
                checked((int)month.Value));
            if (day > maximumDay)
                return false;
        }
        return !value.TryGetPropertyValue("place", out var place)
            || place is JsonObject address && ValidateAddress(address);
    }

    private static bool ValidateNote(JsonObject value)
    {
        if (!HasExpectedType(value, "Note")
            || !HasString(value, "note", out _)
            || value.TryGetPropertyValue("created", out var created)
                && (!TryString(created, out var date) || !JmapDate.TryParseUtcDate(date, out _)))
        {
            return false;
        }
        if (!value.TryGetPropertyValue("author", out var authorNode))
            return true;
        if (authorNode is not JsonObject author || !HasExpectedType(author, "Author"))
            return false;
        var hasName = author.TryGetPropertyValue("name", out var name);
        var hasUri = author.TryGetPropertyValue("uri", out var uri);
        return (hasName || hasUri)
            && (!hasName || TryString(name, out _))
            && (!hasUri || TryAbsoluteUri(uri, out _));
    }

    private static bool ValidatePersonalInfo(JsonObject value) =>
        HasExpectedType(value, "PersonalInfo")
        && HasString(value, "kind", out _)
        && HasString(value, "value", out _)
        && (!value.TryGetPropertyValue("level", out var level) || TryString(level, out _))
        && (!value.TryGetPropertyValue("listAs", out var listAs)
            || TryUnsigned(listAs, out var position) && position > 0)
        && (!value.TryGetPropertyValue("label", out var label) || TryString(label, out _));

    private static bool ValidateCommon(JsonObject value) =>
        (!value.TryGetPropertyValue("contexts", out var contexts) || IsTrueSet(contexts))
        && (!value.TryGetPropertyValue("pref", out var pref)
            || TryUnsigned(pref, out var preference) && preference is >= 1 and <= 100)
        && (!value.TryGetPropertyValue("label", out var label) || TryString(label, out _));

    private static void ValidateObject(
        JsonObject card,
        string property,
        Func<JsonObject, bool> validator,
        ISet<string> invalid)
    {
        if (card.TryGetPropertyValue(property, out var node)
            && (node is not JsonObject value || !validator(value)))
        {
            invalid.Add(property);
        }
    }

    private static void ValidateIdObjectMap(
        JsonObject card,
        string property,
        Func<JsonObject, bool> validator,
        ISet<string> invalid)
    {
        if (card.TryGetPropertyValue(property, out var node)
            && !ValidateIdMapNode(node, validator))
        {
            invalid.Add(property);
        }
    }

    private static bool ValidateIdMapNode(JsonNode? node, Func<JsonObject, bool> validator) =>
        node is JsonObject map
        && map.All(item => JmapId.IsValidId(item.Key)
            && item.Value is JsonObject value
            && validator(value));

    private static void ValidateStringObjectMap(
        JsonObject card,
        string property,
        Func<JsonObject, bool> validator,
        ISet<string> invalid)
    {
        if (card.TryGetPropertyValue(property, out var node)
            && (node is not JsonObject map
                || map.Any(item => item.Key.Length == 0
                    || item.Value is not JsonObject value
                    || !validator(value))))
        {
            invalid.Add(property);
        }
    }

    private static void ValidateLocalizations(JsonObject card, ISet<string> invalid)
    {
        if (!card.TryGetPropertyValue("localizations", out var node))
            return;
        if (node is not JsonObject localizations
            || localizations.Any(item => item.Key.Length == 0 || item.Value is not JsonObject))
        {
            invalid.Add("localizations");
        }
    }

    private static void ValidateOptionalString(
        JsonObject value,
        string property,
        ISet<string> invalid)
    {
        if (value.TryGetPropertyValue(property, out var node) && !TryString(node, out _))
            invalid.Add(property);
    }

    private static void ValidateUtcDate(JsonObject card, string property, ISet<string> invalid)
    {
        if (card.TryGetPropertyValue(property, out var node)
            && (!TryString(node, out var text) || !JmapDate.TryParseUtcDate(text, out _)))
        {
            invalid.Add(property);
        }
    }

    private static void ValidateTrueSet(JsonObject card, string property, ISet<string> invalid)
    {
        if (card.TryGetPropertyValue(property, out var node) && !IsTrueSet(node))
            invalid.Add(property);
    }

    private static bool IsTrueSet(JsonNode? node) => node is JsonObject map
        && map.All(item => item.Value is JsonValue value
            && value.TryGetValue<bool>(out var included)
            && included);

    private static bool IsStringMap(JsonNode? node) => node is JsonObject map
        && map.All(item => TryString(item.Value, out _));

    private static bool HasExpectedType(JsonObject value, string expected) =>
        !value.TryGetPropertyValue("@type", out var node)
        || TryString(node, out var type) && type == expected;

    private static bool HasString(JsonObject value, string property, out string text)
    {
        text = string.Empty;
        return value.TryGetPropertyValue(property, out var node) && TryString(node, out text);
    }

    private static bool TryString(JsonNode? node, out string text)
    {
        text = string.Empty;
        return node is JsonValue value
            && value.TryGetValue<string>(out text!)
            && text is not null;
    }

    private static bool TryBoolean(JsonNode? node, out bool result)
    {
        result = false;
        return node is JsonValue value && value.TryGetValue<bool>(out result);
    }

    private static bool TryUnsigned(JsonNode? node, out long result)
    {
        result = 0;
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<long>(out result))
            return result is >= 0 and <= JmapMethodHelpers.MaximumInt;
        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return result >= 0;
        }
        return false;
    }

    private static bool TryOptionalUnsigned(
        JsonObject value,
        string property,
        out long? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(property, out var node))
            return true;
        if (!TryUnsigned(node, out var number))
            return false;
        result = number;
        return true;
    }

    private static bool TryAbsoluteUri(JsonNode? node, out Uri? uri)
    {
        uri = null;
        return TryString(node, out var text)
            && Uri.TryCreate(text, UriKind.Absolute, out uri);
    }
}
