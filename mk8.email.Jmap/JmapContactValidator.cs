using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;

namespace mk8.email.Jmap;

internal static class JmapContactValidator
{
    private static readonly IReadOnlySet<string> CardProperties = Set(
        "@type", "version", "created", "kind", "language", "members", "prodId",
        "relatedTo", "uid", "updated", "name", "nicknames", "organizations",
        "speakToAs", "titles", "emails", "onlineServices", "phones",
        "preferredLanguages", "calendars", "schedulingAddresses", "addresses",
        "cryptoKeys", "directories", "links", "media", "localizations",
        "anniversaries", "keywords", "notes", "personalInfo");

    private static readonly IReadOnlySet<string> RelationProperties = Set("@type", "relation");
    private static readonly IReadOnlySet<string> NameProperties = Set(
        "@type", "components", "isOrdered", "defaultSeparator", "full", "sortAs",
        "phoneticScript", "phoneticSystem");
    private static readonly IReadOnlySet<string> ComponentProperties = Set(
        "@type", "value", "kind", "phonetic");
    private static readonly IReadOnlySet<string> NicknameProperties = Set(
        "@type", "name", "contexts", "pref");
    private static readonly IReadOnlySet<string> OrganizationProperties = Set(
        "@type", "name", "units", "sortAs", "contexts");
    private static readonly IReadOnlySet<string> OrgUnitProperties = Set("@type", "name", "sortAs");
    private static readonly IReadOnlySet<string> SpeakToAsProperties = Set(
        "@type", "grammaticalGender", "pronouns");
    private static readonly IReadOnlySet<string> PronounsProperties = Set(
        "@type", "pronouns", "contexts", "pref");
    private static readonly IReadOnlySet<string> TitleProperties = Set(
        "@type", "name", "kind", "organizationId");
    private static readonly IReadOnlySet<string> EmailProperties = Set(
        "@type", "address", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> OnlineServiceProperties = Set(
        "@type", "service", "uri", "user", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> PhoneProperties = Set(
        "@type", "number", "features", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> LanguagePrefProperties = Set(
        "@type", "language", "contexts", "pref");
    private static readonly IReadOnlySet<string> ResourceProperties = Set(
        "@type", "kind", "uri", "mediaType", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> CalendarProperties = Set(
        "@type", "kind", "uri", "mediaType", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> DirectoryProperties = Set(
        "@type", "kind", "uri", "mediaType", "contexts", "pref", "label", "listAs");
    private static readonly IReadOnlySet<string> LinkProperties = Set(
        "@type", "kind", "uri", "mediaType", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> MediaProperties = Set(
        "@type", "kind", "uri", "mediaType", "contexts", "pref", "label", "blobId");
    private static readonly IReadOnlySet<string> SchedulingAddressProperties = Set(
        "@type", "uri", "contexts", "pref", "label");
    private static readonly IReadOnlySet<string> AddressProperties = Set(
        "@type", "components", "isOrdered", "countryCode", "coordinates", "timeZone",
        "contexts", "full", "defaultSeparator", "pref", "phoneticScript", "phoneticSystem");
    private static readonly IReadOnlySet<string> AnniversaryProperties = Set(
        "@type", "kind", "date", "place");
    private static readonly IReadOnlySet<string> PartialDateProperties = Set(
        "@type", "year", "month", "day", "calendarScale");
    private static readonly IReadOnlySet<string> TimestampProperties = Set("@type", "utc");
    private static readonly IReadOnlySet<string> NoteProperties = Set(
        "@type", "note", "created", "author");
    private static readonly IReadOnlySet<string> AuthorProperties = Set("@type", "name", "uri");
    private static readonly IReadOnlySet<string> PersonalInfoProperties = Set(
        "@type", "kind", "value", "level", "listAs", "label");

    private static readonly IReadOnlySet<string> KnownProperties = Set(
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
        "id", "addressBookIds", "blobId", "extra");

    private static readonly IReadOnlySet<string> CardKinds = Set(
        "application", "device", "group", "individual", "location", "org");
    private static readonly IReadOnlySet<string> NameComponentKinds = Set(
        "credential", "generation", "given", "given2", "separator", "surname", "surname2", "title");
    private static readonly IReadOnlySet<string> AddressComponentKinds = Set(
        "apartment", "block", "building", "country", "direction", "district", "floor",
        "landmark", "locality", "name", "number", "postOfficeBox", "postcode", "region",
        "room", "separator", "subdistrict");
    private static readonly IReadOnlySet<string> CommonContexts = Set("private", "work");
    private static readonly IReadOnlySet<string> AddressContexts = Set(
        "billing", "delivery", "private", "work");
    private static readonly IReadOnlySet<string> PhoneFeatures = Set(
        "fax", "main-number", "mobile", "pager", "text", "textphone", "video", "voice");
    private static readonly IReadOnlySet<string> GrammaticalGenders = Set(
        "animate", "common", "feminine", "inanimate", "masculine", "neuter");
    private static readonly IReadOnlySet<string> RelationKinds = Set(
        "acquaintance", "agent", "child", "co-resident", "co-worker", "colleague", "contact",
        "crush", "date", "emergency", "friend", "kin", "me", "met", "muse", "neighbor",
        "parent", "sibling", "spouse", "sweetheart");
    private static readonly IReadOnlySet<string> PhoneticSystems = Set("ipa", "jyut", "piny");
    private static readonly IReadOnlySet<string> TitleKinds = Set("role", "title");
    private static readonly IReadOnlySet<string> CalendarKinds = Set("calendar", "freeBusy");
    private static readonly IReadOnlySet<string> DirectoryKinds = Set("directory", "entry");
    private static readonly IReadOnlySet<string> LinkKinds = Set("contact");
    private static readonly IReadOnlySet<string> MediaKinds = Set("logo", "photo", "sound");
    private static readonly IReadOnlySet<string> AnniversaryKinds = Set("birth", "death", "wedding");
    private static readonly IReadOnlySet<string> PersonalInfoKinds = Set("expertise", "hobby", "interest");
    private static readonly IReadOnlySet<string> PersonalInfoLevels = Set("high", "low", "medium");
    private static readonly IReadOnlySet<string> CalendarScales = Set(
        "buddhist", "chinese", "coptic", "dangi", "ethioaa", "ethiopic", "gregory", "hebrew",
        "indian", "islamic", "islamic-civil", "islamic-rgsa", "islamic-tbla",
        "islamic-umalqura", "iso8601", "japanese", "persian", "roc");
    private static readonly IReadOnlySet<string> GrandfatheredLanguageTags = new HashSet<string>(
        [
            "art-lojban", "cel-gaulish", "en-GB-oed", "i-ami", "i-bnn", "i-default",
            "i-enochian", "i-hak", "i-klingon", "i-lux", "i-mingo", "i-navajo", "i-pwn",
            "i-tao", "i-tay", "i-tsu", "no-bok", "no-nyn", "sgn-BE-FR", "sgn-BE-NL",
            "sgn-CH-DE", "zh-guoyu", "zh-hakka", "zh-min", "zh-min-nan", "zh-xiang",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex UtcDatePattern = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]*[1-9])?Z$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool TryValidate(JsonObject card, out IReadOnlyList<string> invalidProperties)
    {
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        ValidateCard(card, invalid, validateLocalizations: true);
        invalidProperties = invalid.Order(StringComparer.Ordinal).ToArray();
        return invalid.Count == 0;
    }

    public static bool IsSupportedCardProperty(string property) =>
        property is "id" or "addressBookIds"
        || CardProperties.Contains(property)
        || !KnownProperties.Contains(property)
            && !KnownProperties.Any(known => string.Equals(
                known,
                property,
                StringComparison.OrdinalIgnoreCase))
            && IsValidExtensionProperty(property);

    private static void ValidateCard(
        JsonObject card,
        ISet<string> invalid,
        bool validateLocalizations)
    {
        ValidatePropertyNames(card, CardProperties, invalid);

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
        if (card.TryGetPropertyValue("kind", out var kind)
            && (!TryString(kind, out var kindText) || !IsEnumValue(kindText, CardKinds)))
        {
            invalid.Add("kind");
        }
        if (card.TryGetPropertyValue("language", out var language)
            && (!TryString(language, out var languageText) || !IsLanguageTag(languageText)))
        {
            invalid.Add("language");
        }
        if (card.TryGetPropertyValue("prodId", out var prodId)
            && (!TryString(prodId, out var product) || product.Length == 0))
        {
            invalid.Add("prodId");
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
        ValidateIdObjectMap(card, "preferredLanguages", ValidateLanguagePref, invalid);
        ValidateIdObjectMap(card, "calendars", ValidateCalendar, invalid);
        ValidateIdObjectMap(card, "schedulingAddresses", ValidateSchedulingAddress, invalid);
        ValidateIdObjectMap(card, "addresses", ValidateAddress, invalid);
        ValidateIdObjectMap(card, "cryptoKeys", ValidateCryptoKey, invalid);
        ValidateIdObjectMap(card, "directories", ValidateDirectory, invalid);
        ValidateIdObjectMap(card, "links", ValidateLink, invalid);
        ValidateIdObjectMap(card, "media", ValidateMedia, invalid);
        ValidateIdObjectMap(card, "anniversaries", ValidateAnniversary, invalid);
        ValidateIdObjectMap(card, "notes", ValidateNote, invalid);
        ValidateIdObjectMap(card, "personalInfo", ValidatePersonalInfo, invalid);
        if (validateLocalizations)
            ValidateLocalizations(card, invalid);
    }

    private static bool ValidateRelation(JsonObject value) =>
        HasExpectedType(value, "Relation")
        && HasValidProperties(value, RelationProperties)
        && (!value.TryGetPropertyValue("relation", out var relation)
            || IsEnumTrueSet(relation, RelationKinds));

    private static bool ValidateName(JsonObject value)
    {
        if (!HasExpectedType(value, "Name")
            || !HasValidProperties(value, NameProperties)
            || value.TryGetPropertyValue("full", out var full) && !TryString(full, out _)
            || value.TryGetPropertyValue("isOrdered", out var ordered) && !TryBoolean(ordered, out _)
            || value.TryGetPropertyValue("defaultSeparator", out var separator)
                && !TryString(separator, out _)
            || value.TryGetPropertyValue("phoneticScript", out var script)
                && (!TryString(script, out var scriptText) || !IsScriptSubtag(scriptText))
            || value.TryGetPropertyValue("phoneticSystem", out var system)
                && (!TryString(system, out var systemText)
                    || !IsEnumValue(systemText, PhoneticSystems)))
        {
            return false;
        }

        var hasFull = value.ContainsKey("full");
        if (!value.TryGetPropertyValue("components", out var componentsNode))
            return hasFull && !value.ContainsKey("defaultSeparator") && !value.ContainsKey("sortAs");
        if (componentsNode is not JsonArray { Count: > 0 } components)
            return false;

        var isOrdered = value["isOrdered"]?.GetValue<bool>() ?? false;
        var hasNonSeparator = false;
        var hasPhonetic = false;
        var previousWasSeparator = false;
        var componentKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var componentNode in components)
        {
            if (componentNode is not JsonObject component
                || !HasExpectedType(component, "NameComponent")
                || !HasValidProperties(component, ComponentProperties)
                || !HasString(component, "kind", out var kind)
                || !IsEnumValue(kind, NameComponentKinds)
                || !HasString(component, "value", out _)
                || component.TryGetPropertyValue("phonetic", out var phonetic)
                    && !TryString(phonetic, out _))
            {
                return false;
            }
            var isSeparator = kind == "separator";
            if (isSeparator && (!isOrdered || previousWasSeparator))
                return false;
            hasNonSeparator |= !isSeparator;
            previousWasSeparator = isSeparator;
            hasPhonetic |= component.ContainsKey("phonetic");
            componentKinds.Add(kind);
        }
        if (!hasNonSeparator
            || !isOrdered && value.ContainsKey("defaultSeparator")
            || hasPhonetic
                && !value.ContainsKey("phoneticScript")
                && !value.ContainsKey("phoneticSystem"))
        {
            return false;
        }
        if (!value.TryGetPropertyValue("sortAs", out var sortAsNode))
            return true;
        return sortAsNode is JsonObject sortAs
            && sortAs.All(item => IsEnumValue(item.Key, NameComponentKinds)
                && componentKinds.Contains(item.Key)
                && TryString(item.Value, out _));
    }

    private static bool ValidateNickname(JsonObject value) =>
        HasExpectedType(value, "Nickname")
        && HasValidProperties(value, NicknameProperties)
        && HasString(value, "name", out _)
        && ValidateContexts(value, CommonContexts)
        && ValidatePref(value);

    private static bool ValidateOrganization(JsonObject value)
    {
        if (!HasExpectedType(value, "Organization")
            || !HasValidProperties(value, OrganizationProperties)
            || value.TryGetPropertyValue("name", out var name) && !TryString(name, out _)
            || value.TryGetPropertyValue("sortAs", out var sortAs) && !TryString(sortAs, out _)
            || !ValidateContexts(value, CommonContexts))
        {
            return false;
        }
        var hasName = value.ContainsKey("name");
        if (!value.TryGetPropertyValue("units", out var unitsNode))
            return hasName;
        return unitsNode is JsonArray { Count: > 0 } units
            && units.All(unit => unit is JsonObject objectValue
                && HasExpectedType(objectValue, "OrgUnit")
                && HasValidProperties(objectValue, OrgUnitProperties)
                && HasString(objectValue, "name", out _)
                && (!objectValue.TryGetPropertyValue("sortAs", out var unitSort)
                    || TryString(unitSort, out _)));
    }

    private static bool ValidateSpeakToAs(JsonObject value)
    {
        if (!HasExpectedType(value, "SpeakToAs")
            || !HasValidProperties(value, SpeakToAsProperties))
        {
            return false;
        }
        var hasGender = value.TryGetPropertyValue("grammaticalGender", out var gender);
        if (hasGender
            && (!TryString(gender, out var genderText)
                || !IsEnumValue(genderText, GrammaticalGenders)))
        {
            return false;
        }
        var hasPronouns = value.TryGetPropertyValue("pronouns", out var pronounsNode);
        if (hasPronouns && !ValidateIdMapNode(pronounsNode, ValidatePronouns))
            return false;
        return hasGender || hasPronouns;
    }

    private static bool ValidatePronouns(JsonObject value) =>
        HasExpectedType(value, "Pronouns")
        && HasValidProperties(value, PronounsProperties)
        && HasString(value, "pronouns", out _)
        && ValidateContexts(value, CommonContexts)
        && ValidatePref(value);

    private static bool ValidateTitle(JsonObject value) =>
        HasExpectedType(value, "Title")
        && HasValidProperties(value, TitleProperties)
        && HasString(value, "name", out _)
        && (!value.TryGetPropertyValue("kind", out var kind)
            || TryString(kind, out var kindText) && IsEnumValue(kindText, TitleKinds))
        && (!value.TryGetPropertyValue("organizationId", out var organizationId)
            || TryString(organizationId, out var id) && JmapId.IsValidId(id));

    private static bool ValidateEmail(JsonObject value)
    {
        if (!HasExpectedType(value, "EmailAddress")
            || !HasValidProperties(value, EmailProperties)
            || !HasString(value, "address", out var address)
            || !MailboxAddress.TryParse(address, out var mailbox)
            || !string.Equals(mailbox.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return ValidateContexts(value, CommonContexts)
            && ValidatePref(value)
            && ValidateLabel(value);
    }

    private static bool ValidateOnlineService(JsonObject value)
    {
        if (!HasExpectedType(value, "OnlineService")
            || !HasValidProperties(value, OnlineServiceProperties)
            || !ValidateContexts(value, CommonContexts)
            || !ValidatePref(value)
            || !ValidateLabel(value))
        {
            return false;
        }
        var hasUri = value.TryGetPropertyValue("uri", out var uriNode);
        var hasUser = value.TryGetPropertyValue("user", out var userNode);
        return (!value.TryGetPropertyValue("service", out var service) || TryString(service, out _))
            && (!hasUri || TryAbsoluteUri(uriNode, out _))
            && (!hasUser || TryString(userNode, out _))
            && (hasUri || hasUser);
    }

    private static bool ValidatePhone(JsonObject value) =>
        HasExpectedType(value, "Phone")
        && HasValidProperties(value, PhoneProperties)
        && HasString(value, "number", out _)
        && (!value.TryGetPropertyValue("features", out var features)
            || IsEnumTrueSet(features, PhoneFeatures))
        && ValidateContexts(value, CommonContexts)
        && ValidatePref(value)
        && ValidateLabel(value);

    private static bool ValidateLanguagePref(JsonObject value) =>
        HasExpectedType(value, "LanguagePref")
        && HasValidProperties(value, LanguagePrefProperties)
        && HasString(value, "language", out var language)
        && IsLanguageTag(language)
        && ValidateContexts(value, CommonContexts)
        && ValidatePref(value);

    private static bool ValidateCalendar(JsonObject value) =>
        ValidateResource(value, "Calendar", CalendarProperties)
        && HasString(value, "kind", out var kind)
        && IsEnumValue(kind, CalendarKinds);

    private static bool ValidateSchedulingAddress(JsonObject value) =>
        HasExpectedType(value, "SchedulingAddress")
        && HasValidProperties(value, SchedulingAddressProperties)
        && TryAbsoluteUri(value["uri"], out _)
        && ValidateContexts(value, CommonContexts)
        && ValidatePref(value)
        && ValidateLabel(value);

    private static bool ValidateAddress(JsonObject value)
    {
        Uri? coordinateUri = null;
        if (!HasExpectedType(value, "Address")
            || !HasValidProperties(value, AddressProperties)
            || !ValidateContexts(value, AddressContexts)
            || !ValidatePref(value)
            || value.TryGetPropertyValue("full", out var full) && !TryString(full, out _)
            || value.TryGetPropertyValue("coordinates", out var coordinates)
                && !TryAbsoluteUri(coordinates, out coordinateUri)
            || value.TryGetPropertyValue("countryCode", out var country)
                && (!TryString(country, out var countryText) || !IsCountryCode(countryText))
            || value.TryGetPropertyValue("timeZone", out var zone)
                && (!TryString(zone, out var zoneText) || !IsTimeZone(zoneText))
            || value.TryGetPropertyValue("isOrdered", out var ordered) && !TryBoolean(ordered, out _)
            || value.TryGetPropertyValue("defaultSeparator", out var separator)
                && !TryString(separator, out _)
            || value.TryGetPropertyValue("phoneticScript", out var script)
                && (!TryString(script, out var scriptText) || !IsScriptSubtag(scriptText))
            || value.TryGetPropertyValue("phoneticSystem", out var system)
                && (!TryString(system, out var systemText)
                    || !IsEnumValue(systemText, PhoneticSystems)))
        {
            return false;
        }
        if (coordinateUri is not null
            && !coordinateUri.Scheme.Equals("geo", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasValue = value.ContainsKey("full")
            || value.ContainsKey("coordinates")
            || value.ContainsKey("countryCode")
            || value.ContainsKey("timeZone");
        if (!value.TryGetPropertyValue("components", out var componentsNode))
            return hasValue && !value.ContainsKey("defaultSeparator");
        if (componentsNode is not JsonArray { Count: > 0 } components)
            return false;

        var isOrdered = value["isOrdered"]?.GetValue<bool>() ?? false;
        var hasNonSeparator = false;
        var hasPhonetic = false;
        var previousWasSeparator = false;
        foreach (var componentNode in components)
        {
            if (componentNode is not JsonObject component
                || !HasExpectedType(component, "AddressComponent")
                || !HasValidProperties(component, ComponentProperties)
                || !HasString(component, "kind", out var kind)
                || !IsEnumValue(kind, AddressComponentKinds)
                || !HasString(component, "value", out _)
                || component.TryGetPropertyValue("phonetic", out var phonetic)
                    && !TryString(phonetic, out _))
            {
                return false;
            }
            var isSeparator = kind == "separator";
            if (isSeparator && (!isOrdered || previousWasSeparator))
                return false;
            hasNonSeparator |= !isSeparator;
            previousWasSeparator = isSeparator;
            hasPhonetic |= component.ContainsKey("phonetic");
        }
        return hasNonSeparator
            && (isOrdered || !value.ContainsKey("defaultSeparator"))
            && (!hasPhonetic
                || value.ContainsKey("phoneticScript")
                || value.ContainsKey("phoneticSystem"));
    }

    private static bool ValidateCryptoKey(JsonObject value) =>
        ValidateResource(value, "CryptoKey", ResourceProperties)
        && (!value.TryGetPropertyValue("kind", out var kind)
            || TryString(kind, out var kindText) && IsVendorValue(kindText));

    private static bool ValidateDirectory(JsonObject value) =>
        ValidateResource(value, "Directory", DirectoryProperties)
        && HasString(value, "kind", out var kind)
        && IsEnumValue(kind, DirectoryKinds)
        && (!value.TryGetPropertyValue("listAs", out var listAs)
            || TryUnsigned(listAs, out var position) && position > 0);

    private static bool ValidateLink(JsonObject value) =>
        ValidateResource(value, "Link", LinkProperties)
        && (!value.TryGetPropertyValue("kind", out var kind)
            || TryString(kind, out var kindText) && IsEnumValue(kindText, LinkKinds));

    private static bool ValidateResource(
        JsonObject value,
        string expectedType,
        IReadOnlySet<string> properties) =>
        HasExpectedType(value, expectedType)
        && HasValidProperties(value, properties)
        && TryAbsoluteUri(value["uri"], out _)
        && (!value.TryGetPropertyValue("mediaType", out var mediaType)
            || TryString(mediaType, out var mediaTypeText) && IsMediaType(mediaTypeText))
        && ValidateContexts(value, CommonContexts)
        && ValidatePref(value)
        && ValidateLabel(value);

    private static bool ValidateMedia(JsonObject value)
    {
        if (!HasExpectedType(value, "Media")
            || !HasValidProperties(value, MediaProperties)
            || !HasString(value, "kind", out var kind)
            || !IsEnumValue(kind, MediaKinds)
            || value.TryGetPropertyValue("mediaType", out var mediaType)
                && (!TryString(mediaType, out var mediaTypeText) || !IsMediaType(mediaTypeText))
            || !ValidateContexts(value, CommonContexts)
            || !ValidatePref(value)
            || !ValidateLabel(value))
        {
            return false;
        }
        var hasUri = value.TryGetPropertyValue("uri", out var uri);
        var hasBlob = value.TryGetPropertyValue("blobId", out var blob);
        return hasUri != hasBlob
            && (!hasUri || TryAbsoluteUri(uri, out _))
            && (!hasBlob || TryString(blob, out var blobId) && JmapId.IsValidId(blobId));
    }

    private static bool ValidateAnniversary(JsonObject value)
    {
        if (!HasExpectedType(value, "Anniversary")
            || !HasValidProperties(value, AnniversaryProperties)
            || !HasString(value, "kind", out var kind)
            || !IsEnumValue(kind, AnniversaryKinds)
            || value["date"] is not JsonObject date)
        {
            return false;
        }
        var dateType = StringValue(date["@type"]);
        var dateIsValid = dateType switch
        {
            null => ValidatePartialDate(date),
            "PartialDate" => ValidatePartialDate(date),
            "Timestamp" => ValidateTimestamp(date),
            _ => false,
        };
        return dateIsValid
            && (!value.TryGetPropertyValue("place", out var place)
                || place is JsonObject address && ValidateAddress(address));
    }

    private static bool ValidatePartialDate(JsonObject value)
    {
        if (!HasExpectedType(value, "PartialDate")
            || !HasValidProperties(value, PartialDateProperties)
            || !TryOptionalUnsigned(value, "year", out var year)
            || !TryOptionalUnsigned(value, "month", out var month)
            || !TryOptionalUnsigned(value, "day", out var day)
            || value.TryGetPropertyValue("calendarScale", out var calendarScale)
                && (!TryString(calendarScale, out var scale) || !IsCalendarScale(scale)))
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
        return month is null
            || day is null
            || day <= DaysInGregorianMonth(year, month.Value);
    }

    private static bool ValidateTimestamp(JsonObject value) =>
        HasExpectedType(value, "Timestamp")
        && HasValidProperties(value, TimestampProperties)
        && HasString(value, "utc", out var utc)
        && IsUtcDateTime(utc);

    private static bool ValidateNote(JsonObject value)
    {
        if (!HasExpectedType(value, "Note")
            || !HasValidProperties(value, NoteProperties)
            || !HasString(value, "note", out _)
            || value.TryGetPropertyValue("created", out var created)
                && (!TryString(created, out var date) || !IsUtcDateTime(date)))
        {
            return false;
        }
        if (!value.TryGetPropertyValue("author", out var authorNode))
            return true;
        if (authorNode is not JsonObject author
            || !HasExpectedType(author, "Author")
            || !HasValidProperties(author, AuthorProperties))
        {
            return false;
        }
        var hasName = author.TryGetPropertyValue("name", out var name);
        var hasUri = author.TryGetPropertyValue("uri", out var uri);
        return (hasName || hasUri)
            && (!hasName || TryString(name, out _))
            && (!hasUri || TryAbsoluteUri(uri, out _));
    }

    private static bool ValidatePersonalInfo(JsonObject value) =>
        HasExpectedType(value, "PersonalInfo")
        && HasValidProperties(value, PersonalInfoProperties)
        && HasString(value, "kind", out var kind)
        && IsEnumValue(kind, PersonalInfoKinds)
        && HasString(value, "value", out _)
        && (!value.TryGetPropertyValue("level", out var level)
            || TryString(level, out var levelText) && IsEnumValue(levelText, PersonalInfoLevels))
        && (!value.TryGetPropertyValue("listAs", out var listAs)
            || TryUnsigned(listAs, out var position) && position > 0)
        && ValidateLabel(value);

    private static void ValidateLocalizations(JsonObject card, ISet<string> invalid)
    {
        if (!card.TryGetPropertyValue("localizations", out var node))
            return;
        if (node is not JsonObject localizations)
        {
            invalid.Add("localizations");
            return;
        }

        var source = (JsonObject)card.DeepClone();
        source.Remove("localizations");
        foreach (var localization in localizations)
        {
            if (!IsLanguageTag(localization.Key)
                || localization.Value is not JsonObject patch
                || !TryApplyLocalizationPatch(source, patch, out var localized))
            {
                invalid.Add("localizations");
                return;
            }
            var localizedInvalid = new HashSet<string>(StringComparer.Ordinal);
            ValidateCard(localized, localizedInvalid, validateLocalizations: false);
            if (localizedInvalid.Count > 0)
            {
                invalid.Add("localizations");
                return;
            }
        }
    }

    private static bool TryApplyLocalizationPatch(
        JsonObject source,
        JsonObject patch,
        out JsonObject result)
    {
        result = (JsonObject)source.DeepClone();
        var parsed = new List<IReadOnlyList<string>>(patch.Count);
        foreach (var item in patch)
        {
            if (!TryParsePatchPath(item.Key, out var path)
                || path[0] == "localizations")
            {
                return false;
            }
            parsed.Add(path);
        }
        for (var left = 0; left < parsed.Count; left++)
        {
            for (var right = left + 1; right < parsed.Count; right++)
            {
                if (IsPathPrefix(parsed[left], parsed[right])
                    || IsPathPrefix(parsed[right], parsed[left]))
                {
                    return false;
                }
            }
        }

        var patchIndex = 0;
        foreach (var item in patch)
        {
            var path = parsed[patchIndex++];
            JsonNode current = result;
            for (var index = 0; index < path.Count - 1; index++)
            {
                if (!TryResolvePatchChild(current, path[index], out var child))
                    return false;
                current = child;
            }

            var finalToken = path[^1];
            if (current is JsonObject parentObject)
            {
                if (item.Value is null)
                    parentObject.Remove(finalToken);
                else
                    parentObject[finalToken] = item.Value.DeepClone();
            }
            else if (current is JsonArray parentArray
                && TryParseArrayIndex(finalToken, parentArray.Count, out var arrayIndex)
                && item.Value is not null)
            {
                parentArray[arrayIndex] = item.Value.DeepClone();
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryResolvePatchChild(JsonNode parent, string token, out JsonNode child)
    {
        child = null!;
        if (parent is JsonObject objectParent)
        {
            return objectParent.TryGetPropertyValue(token, out var value)
                && value is not null
                && (child = value) is not null;
        }
        if (parent is JsonArray arrayParent
            && TryParseArrayIndex(token, arrayParent.Count, out var index)
            && arrayParent[index] is { } arrayValue)
        {
            child = arrayValue;
            return true;
        }
        return false;
    }

    private static bool TryParsePatchPath(string value, out IReadOnlyList<string> path)
    {
        path = [];
        if (value.Length == 0 || value[0] == '/' || value[^1] == '/')
            return false;
        var result = new List<string>();
        foreach (var token in value.Split('/'))
        {
            if (!TryDecodePointerToken(token, out var decoded) || decoded == "-")
                return false;
            result.Add(decoded);
        }
        path = result;
        return true;
    }

    private static bool TryDecodePointerToken(string token, out string decoded)
    {
        var builder = new StringBuilder(token.Length);
        for (var index = 0; index < token.Length; index++)
        {
            if (token[index] != '~')
            {
                builder.Append(token[index]);
                continue;
            }
            if (++index >= token.Length || token[index] is not ('0' or '1'))
            {
                decoded = string.Empty;
                return false;
            }
            builder.Append(token[index] == '0' ? '~' : '/');
        }
        decoded = builder.ToString();
        return true;
    }

    private static bool IsPathPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> value)
    {
        if (prefix.Count >= value.Count)
            return false;
        for (var index = 0; index < prefix.Count; index++)
        {
            if (!string.Equals(prefix[index], value[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool TryParseArrayIndex(string value, int count, out int index)
    {
        index = -1;
        return value.Length > 0
            && (value.Length == 1 || value[0] != '0')
            && value.All(char.IsAsciiDigit)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index >= 0
            && index < count;
    }

    private static void ValidatePropertyNames(
        JsonObject value,
        IReadOnlySet<string> allowed,
        ISet<string> invalid)
    {
        foreach (var property in value.Select(item => item.Key))
        {
            if (!IsAllowedPropertyName(property, allowed))
                invalid.Add(property);
        }
    }

    private static bool HasValidProperties(JsonObject value, IReadOnlySet<string> allowed) =>
        value.All(item => IsAllowedPropertyName(item.Key, allowed));

    private static bool IsAllowedPropertyName(string property, IReadOnlySet<string> allowed) =>
        allowed.Contains(property)
        || !KnownProperties.Contains(property)
            && !KnownProperties.Any(known => string.Equals(
                known,
                property,
                StringComparison.OrdinalIgnoreCase))
            && IsValidExtensionProperty(property);

    private static bool IsValidExtensionProperty(string value)
    {
        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            if (colon == 0 || colon == value.Length - 1)
                return false;
            var labels = value[..colon].Split('.');
            if (labels.Any(label => label.Length == 0
                    || !IsVendorAlphaNumeric(label[0])
                    || !IsVendorAlphaNumeric(label[^1])
                    || label.Any(character => !IsVendorAlphaNumeric(character) && character != '-')))
            {
                return false;
            }
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

    private static bool IsEnumValue(string value, IReadOnlySet<string> registered) =>
        registered.Contains(value) || IsVendorValue(value);

    private static bool IsVendorValue(string value) =>
        value.Contains(':', StringComparison.Ordinal) && IsValidExtensionProperty(value);

    private static bool IsEnumTrueSet(JsonNode? node, IReadOnlySet<string> registered) =>
        node is JsonObject map
        && map.All(item => IsEnumValue(item.Key, registered)
            && item.Value is JsonValue value
            && value.TryGetValue<bool>(out var included)
            && included);

    private static bool ValidateContexts(JsonObject value, IReadOnlySet<string> registered) =>
        !value.TryGetPropertyValue("contexts", out var contexts)
        || IsEnumTrueSet(contexts, registered);

    private static bool ValidatePref(JsonObject value) =>
        !value.TryGetPropertyValue("pref", out var pref)
        || TryUnsigned(pref, out var preference) && preference is >= 1 and <= 100;

    private static bool ValidateLabel(JsonObject value) =>
        !value.TryGetPropertyValue("label", out var label) || TryString(label, out _);

    private static bool IsLanguageTag(string value)
    {
        if (value.Length == 0 || GrandfatheredLanguageTags.Contains(value))
            return value.Length > 0;
        var subtags = value.Split('-');
        if (subtags.Any(subtag => subtag.Length == 0
                || subtag.Length > 8
                || !subtag.All(char.IsAsciiLetterOrDigit)))
        {
            return false;
        }
        if (subtags[0].Equals("x", StringComparison.OrdinalIgnoreCase))
            return subtags.Length > 1;

        var index = 0;
        var language = subtags[index++];
        if (!language.All(char.IsAsciiLetter)
            || language.Length is < 2 or > 8)
        {
            return false;
        }
        if (language.Length is 2 or 3)
        {
            var extlangCount = 0;
            while (index < subtags.Length
                && extlangCount < 3
                && subtags[index].Length == 3
                && subtags[index].All(char.IsAsciiLetter))
            {
                extlangCount++;
                index++;
            }
        }
        if (index < subtags.Length
            && subtags[index].Length == 4
            && subtags[index].All(char.IsAsciiLetter))
        {
            index++;
        }
        if (index < subtags.Length
            && (subtags[index].Length == 2 && subtags[index].All(char.IsAsciiLetter)
                || subtags[index].Length == 3 && subtags[index].All(char.IsAsciiDigit)))
        {
            index++;
        }

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Length && IsLanguageVariant(subtags[index]))
        {
            if (!variants.Add(subtags[index]))
                return false;
            index++;
        }
        var extensions = new HashSet<char>();
        while (index < subtags.Length
            && subtags[index].Length == 1
            && char.IsAsciiLetterOrDigit(subtags[index][0])
            && !subtags[index].Equals("x", StringComparison.OrdinalIgnoreCase))
        {
            var singleton = char.ToLowerInvariant(subtags[index++][0]);
            if (!extensions.Add(singleton))
                return false;
            var start = index;
            while (index < subtags.Length && subtags[index].Length is >= 2 and <= 8)
                index++;
            if (index == start)
                return false;
        }
        if (index < subtags.Length && subtags[index].Equals("x", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            if (index == subtags.Length)
                return false;
            while (index < subtags.Length && subtags[index].Length is >= 1 and <= 8)
                index++;
        }
        return index == subtags.Length;
    }

    private static bool IsLanguageVariant(string value) =>
        value.Length is >= 5 and <= 8
        || value.Length == 4 && char.IsAsciiDigit(value[0]);

    private static bool IsScriptSubtag(string value) =>
        value.Length == 4 && value.All(char.IsAsciiLetter);

    private static bool IsCountryCode(string value)
    {
        if (value.Length != 2 || !value.All(character => character is >= 'A' and <= 'Z'))
            return false;
        try
        {
            return new RegionInfo(value).TwoLetterISORegionName.Equals(
                value,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsTimeZone(string value)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value).Id.Equals(
                value,
                StringComparison.Ordinal);
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool IsCalendarScale(string value) =>
        CalendarScales.Contains(value) || IsVendorValue(value);

    private static bool IsMediaType(string value) =>
        ContentType.TryParse(value, out _);

    private static bool IsUtcDateTime(string value) =>
        UtcDatePattern.IsMatch(value) && JmapDate.TryParseUtcDate(value, out _);

    private static long DaysInGregorianMonth(long? year, long month)
    {
        if (month == 2)
        {
            var calendarYear = year ?? 2000;
            var leap = calendarYear % 4 == 0
                && (calendarYear % 100 != 0 || calendarYear % 400 == 0);
            return leap ? 29 : 28;
        }
        return month is 4 or 6 or 9 or 11 ? 30 : 31;
    }

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
                || map.Any(item => item.Value is not JsonObject value || !validator(value))))
        {
            invalid.Add(property);
        }
    }

    private static void ValidateUtcDate(JsonObject card, string property, ISet<string> invalid)
    {
        if (card.TryGetPropertyValue(property, out var node)
            && (!TryString(node, out var text) || !IsUtcDateTime(text)))
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

    private static bool HasExpectedType(JsonObject value, string expected) =>
        !value.TryGetPropertyValue("@type", out var node)
        || TryString(node, out var type) && type == expected;

    private static bool HasString(JsonObject value, string property, out string text)
    {
        text = string.Empty;
        return value.TryGetPropertyValue(property, out var node) && TryString(node, out text);
    }

    private static string? StringValue(JsonNode? node) =>
        TryString(node, out var value) ? value : null;

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
            && text.Length > 0
            && text.All(character => character <= 0x7f && !char.IsControl(character) && character != ' ')
            && Uri.TryCreate(text, UriKind.Absolute, out uri)
            && uri.IsWellFormedOriginalString();
    }

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.Ordinal);
}
