using System.Globalization;
using System.Text;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

// Presentation-only predicates over a complete independently authorized snapshot.
// A restriction never supplies account/folder scope or bypasses the graph bound.
internal sealed class GatewayEwsFolderRestriction(Func<MailFolderSnapshot, int, bool> predicate)
{
    private readonly Func<MailFolderSnapshot, int, bool> _predicate = predicate;
    internal const int MaximumNodes = 32;
    internal const int MaximumDepth = 8;

    internal bool Matches(MailFolderSnapshot folder, int childCount) => _predicate(folder, childCount);

    internal static GatewayEwsFolderRestriction Parse(XElement restriction)
    {
        GatewayEwsRequestParser.Container(restriction);
        var children = restriction.Elements().ToArray();
        if (children.Length != 1) throw Invalid();
        var nodes = 0;
        return new(Expression(children[0], 0, ref nodes));
    }

    private static Func<MailFolderSnapshot, int, bool> Expression(XElement expression, int depth, ref int nodes)
    {
        if (++nodes > MaximumNodes || depth > MaximumDepth) throw Unsupported();
        if (expression.Name.Namespace != GatewayEwsSoap.Types) throw Invalid();
        var name = expression.Name.LocalName;
        if (name is "Contains") return Contains(expression);
        GatewayEwsRequestParser.Container(expression);
        var fields = expression.Elements().ToArray();
        if (name is "And" or "Or" or "Not")
        {
            if (name is "Not" ? fields.Length != 1 : fields.Length < 2) throw Invalid();
            var conditions = new Func<MailFolderSnapshot, int, bool>[fields.Length];
            for (var index = 0; index < fields.Length; index++)
                conditions[index] = Expression(fields[index], depth + 1, ref nodes);
            return name switch
            {
                "And" => (folder, count) => conditions.All(condition => condition(folder, count)),
                "Or" => (folder, count) => conditions.Any(condition => condition(folder, count)),
                _ => (folder, count) => !conditions[0](folder, count),
            };
        }
        if (name is not ("IsEqualTo" or "IsNotEqualTo" or "IsGreaterThan" or "IsGreaterThanOrEqualTo"
            or "IsLessThan" or "IsLessThanOrEqualTo"))
        {
            throw Unsupported();
        }
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Types + "FieldURI"
            || fields[1].Name != GatewayEwsSoap.Types + "FieldURIOrConstant")
        {
            throw Invalid();
        }
        GatewayEwsRequestParser.Empty(fields[0], "FieldURI");
        GatewayEwsRequestParser.Container(fields[1]);
        var constants = fields[1].Elements().ToArray();
        if (constants.Length != 1 || constants[0].Name != GatewayEwsSoap.Types + "Constant") throw Unsupported();
        var value = Constant(constants[0]);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid();
        Func<MailFolderSnapshot, int, int> read = ((string?)fields[0].Attribute("FieldURI")) switch
        {
            "folder:TotalCount" => (folder, _) => folder.TotalEmails,
            "folder:UnreadCount" => (folder, _) => folder.UnreadEmails,
            "folder:ChildFolderCount" => (_, count) => count,
            _ => throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest"),
        };
        return name switch
        {
            "IsEqualTo" => (folder, count) => read(folder, count) == number,
            "IsNotEqualTo" => (folder, count) => read(folder, count) != number,
            "IsGreaterThan" => (folder, count) => read(folder, count) > number,
            "IsGreaterThanOrEqualTo" => (folder, count) => read(folder, count) >= number,
            "IsLessThan" => (folder, count) => read(folder, count) < number,
            _ => (folder, count) => read(folder, count) <= number,
        };
    }

    private static Func<MailFolderSnapshot, int, bool> Contains(XElement expression)
    {
        GatewayEwsRequestParser.Container(expression, "ContainmentMode", "ContainmentComparison");
        var mode = (string?)expression.Attribute("ContainmentMode");
        var comparison = (string?)expression.Attribute("ContainmentComparison");
        if (mode is not ("FullString" or "Prefixed" or "Substring") || comparison is not ("Exact" or "IgnoreCase"))
            throw Unsupported();
        var fields = expression.Elements().ToArray();
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Types + "FieldURI"
            || fields[1].Name != GatewayEwsSoap.Types + "Constant")
        {
            throw Invalid();
        }
        GatewayEwsRequestParser.Empty(fields[0], "FieldURI");
        if ((string?)fields[0].Attribute("FieldURI") is not "folder:DisplayName")
            throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest");
        var value = Constant(fields[1]);
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > 255) throw Unsupported();
        var collation = comparison is "Exact" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Preserve the decoded constant; do not trim, normalize accents or words.
        return mode switch
        {
            "FullString" => (folder, _) => string.Equals(folder.Name, value, collation),
            "Prefixed" => (folder, _) => folder.Name.StartsWith(value, collation),
            _ => (folder, _) => folder.Name.Contains(value, collation),
        };
    }

    private static string Constant(XElement element)
    {
        GatewayEwsRequestParser.Empty(element, "Value");
        return (string?)element.Attribute("Value") ?? throw Invalid();
    }

    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
    private static GatewayEwsRequestException Unsupported() => new("ErrorInvalidRequest");
}
