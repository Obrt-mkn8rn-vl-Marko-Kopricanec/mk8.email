using System.Globalization;
using System.Xml.Linq;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsItemSearchParser
{
    internal const int MaximumNodes = 32;
    internal const int MaximumDepth = 8;
    private static readonly string[] DateFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];

    internal static MailMessageFilter Restriction(XElement restriction)
    {
        GatewayEwsRequestParser.Container(restriction);
        var children = restriction.Elements().ToArray();
        if (children.Length != 1) throw Unsupported();
        var nodes = 0;
        return Expression(children[0], 0, ref nodes);
    }

    private static MailMessageFilter Expression(XElement expression, int depth, ref int nodes)
    {
        if (++nodes > MaximumNodes || depth > MaximumDepth) throw Unsupported();
        if (expression.Name.Namespace != GatewayEwsSoap.Types) throw Invalid();
        GatewayEwsRequestParser.Container(expression);
        var name = expression.Name.LocalName;
        var fields = expression.Elements().ToArray();
        if (name is "And" or "Or" or "Not")
        {
            if (name is "Not" ? fields.Length != 1 : fields.Length < 2) throw Invalid();
            var conditions = new MailMessageFilter[fields.Length];
            for (var index = 0; index < fields.Length; index++) conditions[index] = Expression(fields[index], depth + 1, ref nodes);
            return new(name is "And" ? MailMessageFilterOperator.And : name is "Or" ? MailMessageFilterOperator.Or : MailMessageFilterOperator.Not,
                conditions, null);
        }
        if (name is not ("IsEqualTo" or "IsNotEqualTo" or "IsGreaterThan" or "IsGreaterThanOrEqualTo" or "IsLessThan" or "IsLessThanOrEqualTo"))
            throw Unsupported();
        if (fields.Length != 2 || fields[0].Name != GatewayEwsSoap.Types + "FieldURI"
            || fields[1].Name != GatewayEwsSoap.Types + "FieldURIOrConstant") throw Invalid();
        GatewayEwsRequestParser.Empty(fields[0], "FieldURI");
        GatewayEwsRequestParser.Container(fields[1]);
        var constants = fields[1].Elements().ToArray();
        if (constants.Length != 1 || constants[0].Name != GatewayEwsSoap.Types + "Constant") throw Unsupported();
        GatewayEwsRequestParser.Empty(constants[0], "Value");
        var value = (string?)constants[0].Attribute("Value") ?? throw Invalid();
        return ((string?)fields[0].Attribute("FieldURI")) switch
        {
            "message:IsRead" => Boolean(name, value, "$seen"),
            "item:IsDraft" => Boolean(name, value, "$draft"),
            "item:Size" => Size(name, value),
            "item:DateTimeReceived" => Received(name, value),
            _ => throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest"),
        };
    }

    private static MailMessageFilter Boolean(string comparison, string value, string keyword)
    {
        if (comparison is not ("IsEqualTo" or "IsNotEqualTo")) throw Unsupported();
        if (value is not ("true" or "1" or "false" or "0")) throw Invalid();
        var positive = (value is "true" or "1") == (comparison is "IsEqualTo");
        return Term(new(positive ? MailMessageFilterField.HasKeyword : MailMessageFilterField.NotKeyword,
            keyword, null, null, null, null, null));
    }

    private static MailMessageFilter Size(string comparison, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)) throw Invalid();
        var lower = Term(new(MailMessageFilterField.MinSize, null, null, null, null, size, null));
        // Widen before addition: EWS Size is a nonnegative Int32; native bounds
        // are Int64, so the inclusive Int32.MaxValue endpoint cannot overflow.
        var upper = Term(new(MailMessageFilterField.MaxSize, null, null, null, null, (long)size + 1, null));
        var equal = new MailMessageFilter(MailMessageFilterOperator.And, [lower, upper], null);
        return comparison switch
        {
            "IsEqualTo" => equal,
            "IsNotEqualTo" => new(MailMessageFilterOperator.Not, [equal], null),
            "IsGreaterThan" => Term(new(MailMessageFilterField.MinSize, null, null, null, null, (long)size + 1, null)),
            "IsGreaterThanOrEqualTo" => lower,
            "IsLessThan" => Term(new(MailMessageFilterField.MaxSize, null, null, null, null, size, null)),
            _ => upper,
        };
    }

    private static MailMessageFilter Received(string comparison, string value)
    {
        // Admit only the two exact native inequalities, with an explicit zone.
        // Refuse other comparisons instead of silently rounding timestamp ticks.
        if (comparison is not ("IsGreaterThanOrEqualTo" or "IsLessThan")) throw Unsupported();
        var fraction = value.IndexOf('.', StringComparison.Ordinal);
        if (fraction >= 0 && (fraction + 1 == value.Length || !char.IsAsciiDigit(value[fraction + 1]))) throw Invalid();
        if (value.Length > 33 || !DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var received)) throw Invalid();
        return Term(new(comparison is "IsLessThan" ? MailMessageFilterField.Before : MailMessageFilterField.After,
            null, null, null, received.UtcDateTime, null, null));
    }

    internal static IReadOnlyList<MailMessageSort> Sort(XElement sort)
    {
        GatewayEwsRequestParser.Container(sort);
        var fields = sort.Elements().ToArray();
        if (fields.Length is < 1 or > 2) throw Unsupported();
        var result = new List<MailMessageSort>(fields.Length);
        foreach (var field in fields)
        {
            if (field.Name != GatewayEwsSoap.Types + "FieldOrder") throw Invalid();
            GatewayEwsRequestParser.Container(field, "Order");
            var order = (string?)field.Attribute("Order");
            if (order is not ("Ascending" or "Descending")) throw Invalid();
            var children = field.Elements().ToArray();
            if (children.Length != 1 || children[0].Name != GatewayEwsSoap.Types + "FieldURI") throw Unsupported();
            GatewayEwsRequestParser.Empty(children[0], "FieldURI");
            var native = ((string?)children[0].Attribute("FieldURI")) switch
            {
                "item:DateTimeReceived" => MailMessageSortField.ReceivedAt,
                "item:Size" => MailMessageSortField.Size,
                _ => throw new GatewayEwsRequestException("ErrorInvalidPropertyRequest"),
            };
            if (result.Any(item => item.Field == native)) throw Unsupported();
            result.Add(new(native, order is "Ascending", null, MailStringCollation.UnicodeCasemap));
        }
        return result;
    }

    private static MailMessageFilter Term(MailMessageFilterTerm term) => new(MailMessageFilterOperator.Condition, null, [term]);
    private static GatewayEwsRequestException Unsupported() => new("ErrorInvalidRequest");
    private static GatewayEwsRequestException Invalid() => new("ErrorSchemaValidation");
}
