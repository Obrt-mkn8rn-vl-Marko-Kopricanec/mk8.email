using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[SuppressMessage("Performance", "CA1812", Justification = "MSTest discovers and executes this fixture by reflection.")]
internal sealed class GatewayEwsItemSearchTests
{
    [TestMethod]
    [DataRow("message:IsRead", "IsEqualTo", "true", "$seen", true)]
    [DataRow("message:IsRead", "IsEqualTo", "0", "$seen", false)]
    [DataRow("message:IsRead", "IsNotEqualTo", "false", "$seen", true)]
    [DataRow("message:IsRead", "IsNotEqualTo", "1", "$seen", false)]
    [DataRow("item:IsDraft", "IsEqualTo", "1", "$draft", true)]
    [DataRow("item:IsDraft", "IsEqualTo", "false", "$draft", false)]
    [DataRow("item:IsDraft", "IsNotEqualTo", "0", "$draft", true)]
    [DataRow("item:IsDraft", "IsNotEqualTo", "true", "$draft", false)]
    public async Task ItemSearchBooleanConstantsMapOnlyToSpecificKeywordTerms(string field, string comparison, string value, string keyword, bool positive)
    {
        var request = await ParseAsync(Restriction(Comparison(field, comparison, value))).ConfigureAwait(false);
        var term = request.Restriction!.Terms!.Single();
        Assert.AreEqual(positive ? MailMessageFilterField.HasKeyword : MailMessageFilterField.NotKeyword, term.Field);
        Assert.AreEqual(keyword, term.Text, StringComparer.Ordinal);
        Assert.IsNull(term.UtcDate);
        Assert.IsNull(term.Number);
        Assert.IsFalse(request.IsMutation);
    }

    [TestMethod]
    [DataRow("IsEqualTo")]
    [DataRow("IsNotEqualTo")]
    [DataRow("IsGreaterThan")]
    [DataRow("IsGreaterThanOrEqualTo")]
    [DataRow("IsLessThan")]
    [DataRow("IsLessThanOrEqualTo")]
    public async Task ItemSearchSizeRelationsAreExactAtBothEnds(string comparison)
    {
        foreach (var constant in new[] { 0, 7, int.MaxValue })
        {
            var request = await ParseAsync(Restriction(Comparison("item:Size", comparison,
                constant.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ConfigureAwait(false);
            foreach (var candidate in new long[] { 0, 6, 7, 8, int.MaxValue })
            {
                var expected = comparison switch
                {
                    "IsEqualTo" => candidate == constant,
                    "IsNotEqualTo" => candidate != constant,
                    "IsGreaterThan" => candidate > constant,
                    "IsGreaterThanOrEqualTo" => candidate >= constant,
                    "IsLessThan" => candidate < constant,
                    _ => candidate <= constant,
                };
                Assert.AreEqual(expected, MatchesSize(request.Restriction!, candidate), comparison);
            }
        }
    }

    [TestMethod]
    [DataRow("IsGreaterThanOrEqualTo", "2026-10-08T12:00:00Z", "2026-10-08T12:00:00.0000000Z")]
    [DataRow("IsLessThan", "2026-10-08T14:00:00+02:00", "2026-10-08T12:00:00.0000000Z")]
    [DataRow("IsLessThan", "2026-10-08T08:00:00.1234567-04:00", "2026-10-08T12:00:00.1234567Z")]
    [DataRow("IsGreaterThanOrEqualTo", "0001-01-01T00:00:00Z", "0001-01-01T00:00:00.0000000Z")]
    [DataRow("IsLessThan", "9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.9999999Z")]
    public async Task ItemSearchReceivedBoundsRetainExplicitZoneAndTickPrecision(string comparison, string value, string utc)
    {
        var request = await ParseAsync(Restriction(Comparison("item:DateTimeReceived", comparison, value))).ConfigureAwait(false);
        var term = request.Restriction!.Terms!.Single();
        Assert.AreEqual(comparison is "IsLessThan" ? MailMessageFilterField.Before : MailMessageFilterField.After, term.Field);
        Assert.AreEqual(DateTimeKind.Utc, term.UtcDate!.Value.Kind);
        Assert.AreEqual(utc, term.UtcDate.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("item:Size", "IsEqualTo", "-1", "ErrorSchemaValidation")]
    [DataRow("item:Size", "IsLessThan", "2147483648", "ErrorSchemaValidation")]
    [DataRow("item:Size", "IsEqualTo", "1.0", "ErrorSchemaValidation")]
    [DataRow("item:Size", "IsEqualTo", "+1", "ErrorSchemaValidation")]
    [DataRow("message:IsRead", "IsEqualTo", "True", "ErrorSchemaValidation")]
    [DataRow("item:IsDraft", "IsEqualTo", "2", "ErrorSchemaValidation")]
    [DataRow("message:IsRead", "IsGreaterThan", "true", "ErrorInvalidRequest")]
    [DataRow("item:DateTimeReceived", "IsEqualTo", "2026-10-08T12:00:00Z", "ErrorInvalidRequest")]
    [DataRow("item:DateTimeReceived", "IsGreaterThan", "2026-10-08T12:00:00Z", "ErrorInvalidRequest")]
    [DataRow("item:DateTimeReceived", "IsLessThanOrEqualTo", "2026-10-08T12:00:00Z", "ErrorInvalidRequest")]
    [DataRow("item:DateTimeReceived", "IsLessThan", "2026-10-08T12:00:00", "ErrorSchemaValidation")]
    [DataRow("item:DateTimeReceived", "IsLessThan", "2026-02-30T12:00:00Z", "ErrorSchemaValidation")]
    [DataRow("item:DateTimeReceived", "IsLessThan", "2026-10-08T12:00:00.12345678Z", "ErrorSchemaValidation")]
    [DataRow("item:DateTimeReceived", "IsLessThan", "2026-10-08T12:00:00.Z", "ErrorSchemaValidation")]
    [DataRow("item:DateTimeReceived", "IsLessThan", "2026-10-08T12:00:00.+02:00", "ErrorSchemaValidation")]
    [DataRow("item:DateTimeReceived", "IsLessThan", "2026-10-08T12:00:00+15:00", "ErrorSchemaValidation")]
    [DataRow("item:HasAttachments", "IsEqualTo", "true", "ErrorInvalidPropertyRequest")]
    [DataRow("item:Subject", "IsEqualTo", "text", "ErrorInvalidPropertyRequest")]
    [DataRow("item:ParentFolderId", "IsEqualTo", "foreign", "ErrorInvalidPropertyRequest")]
    public async Task ItemSearchUnsupportedOrUnrepresentableConstantsRefuse(string field, string comparison, string value, string code)
    {
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(Restriction(Comparison(field, comparison, value)))).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow("<m:Restriction/>", "ErrorInvalidRequest")]
    [DataRow("<m:Restriction><t:Not/></m:Restriction>", "ErrorSchemaValidation")]
    [DataRow("<m:Restriction><t:And><t:Exists/></t:And></m:Restriction>", "ErrorSchemaValidation")]
    [DataRow("<m:Restriction><t:Contains/></m:Restriction>", "ErrorInvalidRequest")]
    [DataRow("<m:Restriction><m:IsEqualTo/></m:Restriction>", "ErrorSchemaValidation")]
    [DataRow("<m:Restriction><t:IsEqualTo Extra='x'/></m:Restriction>", "ErrorSchemaValidation")]
    [DataRow("<m:Restriction><t:IsEqualTo><t:FieldURI FieldURI='item:Size'/><t:FieldURIOrConstant><t:FieldURI FieldURI='item:Size'/></t:FieldURIOrConstant></t:IsEqualTo></m:Restriction>", "ErrorInvalidRequest")]
    [DataRow("<m:Restriction><t:IsEqualTo><t:FieldURI FieldURI='item:Size'/><t:FieldURIOrConstant><t:Constant/></t:FieldURIOrConstant></t:IsEqualTo></m:Restriction>", "ErrorSchemaValidation")]
    [DataRow("<m:SortOrder/>", "ErrorInvalidRequest")]
    [DataRow("<m:SortOrder><t:FieldOrder Order='ascending'><t:FieldURI FieldURI='item:Size'/></t:FieldOrder></m:SortOrder>", "ErrorSchemaValidation")]
    [DataRow("<m:SortOrder><t:FieldOrder Order='Ascending'><t:FieldURI FieldURI='item:Subject'/></t:FieldOrder></m:SortOrder>", "ErrorInvalidPropertyRequest")]
    [DataRow("<m:SortOrder><t:FieldOrder Order='Ascending'><t:ExtendedFieldURI PropertyTag='0x0E08'/></t:FieldOrder></m:SortOrder>", "ErrorInvalidRequest")]
    public async Task ItemSearchMalformedOrUnsupportedExpressionShapesRefuse(string fields, string code)
    {
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(fields)).ConfigureAwait(false);
        Assert.AreEqual(code, error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ItemSearchExplicitSortPreservesComparatorOrderAndDirection(bool ascending)
    {
        var order = ascending ? "Ascending" : "Descending";
        var fields = $"<m:SortOrder><t:FieldOrder Order='{order}'><t:FieldURI FieldURI='item:Size'/></t:FieldOrder><t:FieldOrder Order='Ascending'><t:FieldURI FieldURI='item:DateTimeReceived'/></t:FieldOrder></m:SortOrder>";
        var request = await ParseAsync(fields).ConfigureAwait(false);
        Assert.HasCount(2, request.SortOrder!);
        Assert.AreEqual(MailMessageSortField.Size, request.SortOrder![0].Field);
        Assert.AreEqual(ascending, request.SortOrder[0].IsAscending);
        Assert.AreEqual(MailMessageSortField.ReceivedAt, request.SortOrder[1].Field);
        Assert.IsTrue(request.SortOrder[1].IsAscending);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(fields.Replace("item:DateTimeReceived", "item:Size", StringComparison.Ordinal))).ConfigureAwait(false);
        Assert.AreEqual("ErrorInvalidRequest", error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ItemSearchExpressionNodeAndDepthBoundsAreEnforced(bool depth)
    {
        var leaf = Comparison("item:Size", "IsEqualTo", "7");
        var expression = depth ? string.Concat(Enumerable.Repeat("<t:Not>", 8)) + leaf + string.Concat(Enumerable.Repeat("</t:Not>", 8))
            : "<t:Or>" + string.Concat(Enumerable.Repeat(leaf, 31)) + "</t:Or>";
        _ = await ParseAsync(Restriction(expression)).ConfigureAwait(false);
        var tooLarge = depth ? "<t:Not>" + expression + "</t:Not>"
            : expression.Replace("</t:Or>", leaf + "</t:Or>", StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(Restriction(tooLarge))).ConfigureAwait(false);
        Assert.AreEqual("ErrorInvalidRequest", error.Code, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task ItemSearchExpressionsCannotChangeRegisteredFieldOrderOrDuplicateSingletons()
    {
        var restriction = Restriction(Comparison("item:Size", "IsEqualTo", "7"));
        var sort = "<m:SortOrder><t:FieldOrder Order='Ascending'><t:FieldURI FieldURI='item:Size'/></t:FieldOrder></m:SortOrder>";
        foreach (var fields in new[] { sort + restriction, restriction + restriction, sort + sort })
        {
            var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => ParseAsync(fields)).ConfigureAwait(false);
            Assert.AreEqual("ErrorInvalidRequest", error.Code, StringComparer.Ordinal);
        }
        var request = await ParseAsync(restriction + sort).ConfigureAwait(false);
        Assert.IsNotNull(request.Restriction);
        Assert.IsNotNull(request.SortOrder);
    }

    [TestMethod]
    public async Task ItemSearchBooleanExpressionStaysInsideIndependentAccountAndFolderScope()
    {
        var account = Guid.CreateVersion7();
        var folder = Guid.CreateVersion7();
        var expression = "<t:Not><t:Or>" + Comparison("message:IsRead", "IsEqualTo", "true")
            + Comparison("item:IsDraft", "IsEqualTo", "true") + "</t:Or></t:Not>";
        var parsed = await ParseAsync(Restriction(expression)).ConfigureAwait(false);
        var transport = new QueryTransport();
        var application = new GatewayEwsClient(transport, new EnvironmentConfig());
        var profile = new JmapApplicationProfile("owner@example.test", new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{account:N}", "owner@example.test", true, false, true)]);
        _ = await application.QueryItemsAsync(new(ProtocolAuthenticationKinds.Password, "owner@example.test", "secret"), profile, account, folder,
            0, 2, CancellationToken.None, parsed.Restriction, parsed.SortOrder).ConfigureAwait(false);
        var command = transport.Command!;
        Assert.AreEqual(account, command.AccountId);
        Assert.AreEqual(MailMessageFilterOperator.And, command.Criteria.Filter!.Operator);
        var scope = command.Criteria.Filter.Conditions![0];
        Assert.AreEqual(MailMessageFilterField.InMailbox, scope.Terms!.Single().Field);
        Assert.AreEqual($"M{folder:N}", scope.Terms!.Single().Text, StringComparer.Ordinal);
        Assert.AreEqual(MailMessageFilterOperator.Not, command.Criteria.Filter.Conditions[1].Operator);
        Assert.IsFalse(command.Criteria.CollapseThreads);
        Assert.IsFalse(command.CheckAccountOnly);
        Assert.IsNull(command.AnchorId);
        Assert.AreEqual(MailMessageSortField.ReceivedAt, command.Criteria.Sort.Single().Field);
        Assert.IsFalse(command.Criteria.Sort.Single().IsAscending);
    }

    private static bool MatchesSize(MailMessageFilter filter, long size) => filter.Operator switch
    {
        MailMessageFilterOperator.And => filter.Conditions!.All(child => MatchesSize(child, size)),
        MailMessageFilterOperator.Or => filter.Conditions!.Any(child => MatchesSize(child, size)),
        MailMessageFilterOperator.Not => !MatchesSize(filter.Conditions!.Single(), size),
        _ => filter.Terms!.All(term => term.Field is MailMessageFilterField.MinSize ? size >= term.Number : size < term.Number),
    };
    internal static string Comparison(string field, string comparison, string value) => $"<t:{comparison}><t:FieldURI FieldURI='{field}'/><t:FieldURIOrConstant><t:Constant Value='{value}'/></t:FieldURIOrConstant></t:{comparison}>";
    private static string Restriction(string expression) => "<m:Restriction>" + expression + "</m:Restriction>";
    private static async Task<GatewayEwsRequest> ParseAsync(string fields)
    {
        var xml = $"<s:Envelope xmlns:s='{GatewayEwsSoap.Soap}' xmlns:m='{GatewayEwsSoap.Messages}' xmlns:t='{GatewayEwsSoap.Types}'><s:Body><m:FindItem Traversal='Shallow'><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape></m:ItemShape>{fields}<m:ParentFolderIds><t:DistinguishedFolderId Id='inbox'/></m:ParentFolderIds></m:FindItem></s:Body></s:Envelope>";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await GatewayEwsRequestParser.ReadAsync(body, CancellationToken.None).ConfigureAwait(false);
    }
    private sealed class QueryTransport : IGatewayApplicationTransport
    {
        internal MailMessageQueryCommand? Command { get; private set; }
        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            var input = (MailOperationApplicationRequest)(object)request!;
            Assert.AreEqual(MailOperationKind.FindMessages, input.Command.Operation);
            Command = input.Command.Arguments.Deserialize<MailMessageQueryCommand>(JsonSerializerOptions.Web);
            var reply = new JmapApplicationResult(JmapApplicationOutcomes.Ok, OperationResult: new(new(MailOperationKind.FindMessages,
                ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(new MailMessageQueryResult(MailMessageQueryStatus.Ok, "s1", 0, [], 0), JsonSerializerOptions.Web))),
                new Dictionary<string, string>(StringComparer.Ordinal), new("owner@example.test", new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [])));
            // Match the already admitted profile without granting any new account.
            reply = reply with { OperationResult = reply.OperationResult! with { Profile = reply.OperationResult.Profile with { Accounts = [new($"A{Command!.AccountId:N}", "owner@example.test", true, false, true)] } } };
            return Task.FromResult((TResponse)(object)reply);
        }
    }
}
