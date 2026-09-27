using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using MimeKit.Utils;

namespace mk8.email.Jmap;

internal sealed record JmapBuildResult(JmapBuiltMessage? Value, JsonObject? Error)
{
    public static JmapBuildResult Failed(
        string type,
        string? description = null,
        IEnumerable<string>? properties = null) =>
        new(null, JmapMethodHelpers.SetError(type, description, properties));
}
