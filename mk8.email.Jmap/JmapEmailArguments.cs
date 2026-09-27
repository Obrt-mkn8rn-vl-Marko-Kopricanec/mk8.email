using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal static class JmapEmailArguments
{
    public static bool TryGetProjectionOptions(
        JsonObject arguments,
        IReadOnlyList<string> defaults,
        bool allowNullProperties,
        out JmapEmailProjectionOptions options)
    {
        options = null!;
        if (!JmapMethodHelpers.TryGetStringArray(
                arguments,
                "properties",
                allowNullProperties,
                out var properties)
            || !JmapMethodHelpers.TryGetStringArray(arguments, "bodyProperties", false, out var bodyProperties)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "fetchTextBodyValues", false, out var fetchText)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "fetchHTMLBodyValues", false, out var fetchHtml)
            || !JmapMethodHelpers.TryGetOptionalBoolean(arguments, "fetchAllBodyValues", false, out var fetchAll)
            || !JmapMethodHelpers.TryGetOptionalUnsignedInt(
                arguments,
                "maxBodyValueBytes",
                out var maximumBodyBytes,
                allowNull: false))
        {
            return false;
        }

        var selectedProperties = properties ?? defaults;
        var selectedBodyProperties = bodyProperties ?? JmapEmailCodec.DefaultBodyProperties;
        if (!JmapEmailCodec.TryValidateProperties(selectedProperties, bodyProperties: false, out _)
            || !JmapEmailCodec.TryValidateProperties(selectedBodyProperties, bodyProperties: true, out _))
        {
            return false;
        }

        options = new JmapEmailProjectionOptions(
            selectedProperties,
            selectedBodyProperties,
            fetchText,
            fetchHtml,
            fetchAll,
            checked((int)Math.Min(maximumBodyBytes ?? 0, int.MaxValue)));
        return true;
    }

    public static bool TryGetIds(
        JsonObject arguments,
        string property,
        bool nullable,
        out IReadOnlyList<string>? ids)
        => JmapMethodHelpers.TryGetIdArray(
            arguments,
            property,
            nullable,
            out ids);
}
