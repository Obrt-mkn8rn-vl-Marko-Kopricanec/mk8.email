using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class GatewayVacationSetCodecTests
{
    private const string AccountId = "A11111111111111111111111111111111";

    [TestMethod]
    [DataRow("2026-09-29T12:34:56Z")]
    [DataRow("2026-09-29T12:34:56.1234567Z")]
    [DataRow("2026-09-29T12:34:56.123456789Z")]
    [DataRow("2026-09-29T12:34:56.000Z")]
    [DataRow("2024-12-31T23:59:60Z")]
    [DataRow("2026-09-29T12:34:56+01:00")]
    [DataRow("2026-09-29T12:34:56z")]
    [DataRow("2026-09-29T12:34:56.0Z")]
    public void GatewayUtcDateParsingMatchesLegacyWireAcceptance(string value)
    {
        var expected = JmapDate.TryParseUtcDate(value, out var parsed);
        var actual = GatewayJmapDateCodec.TryParseUtc(value, out var date);
        Assert.AreEqual(expected, actual);
        if (expected) Assert.AreEqual(parsed.UtcDateTime, date);
    }

    [TestMethod]
    public void GatewayParsesTypedVacationMutationAndRendersMixedOutcomes()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["ifInState"] = "s1",
            ["create"] = new JsonObject { ["new"] = new JsonObject { ["isEnabled"] = true } },
            ["update"] = new JsonObject
            {
                ["#made"] = new JsonObject
                {
                    ["isEnabled"] = true,
                    ["fromDate"] = "2026-09-29T12:34:56Z",
                    ["subject"] = "Away",
                    ["textBody"] = "Back later",
                },
                ["missing"] = new JsonObject { ["subject"] = "Ignored" },
            },
            ["destroy"] = new JsonArray("#made", "missing"),
        };
        var known = new Dictionary<string, string>(StringComparer.Ordinal) { ["made"] = "singleton" };
        Assert.IsTrue(GatewayVacationSetCodec.TryParse(arguments, known, 5, out var call, out var failure));
        Assert.IsNull(failure);
        Assert.IsNotNull(call);
        Assert.AreEqual(Guid.Parse("11111111-1111-1111-1111-111111111111"), call.Command.AccountId);
        Assert.AreEqual("s1", call.Command.IfInState);
        Assert.HasCount(1, call.Command.Updates);
        Assert.IsTrue(call.Command.Updates[0].IsEnabled);
        Assert.AreEqual(new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc),
            call.Command.Updates[0].FromDate);
        var rendered = GatewayVacationSetCodec.Render(call,
            new MailVacationSetResult(MailVacationSetStatus.Ok, "s1", "s2", [new(true, [])]));
        Assert.AreEqual(MailOperationKind.MutateVacationSettings, rendered.Operation);
        Assert.AreEqual("s1", rendered.Data["oldState"]!.GetValue<string>());
        Assert.IsTrue(rendered.Data["updated"]!.AsObject().ContainsKey("singleton"));
        Assert.AreEqual("singleton", rendered.Data["notCreated"]!["new"]!["type"]!.GetValue<string>());
        Assert.AreEqual("notFound", rendered.Data["notUpdated"]!["missing"]!["type"]!.GetValue<string>());
        Assert.AreEqual("singleton", rendered.Data["notDestroyed"]!["#made"]!["type"]!.GetValue<string>());
        Assert.AreEqual("notFound", rendered.Data["notDestroyed"]!["missing"]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayClassifiesPatchAndValueErrorsWithoutSendingThemToWorker()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = AccountId,
            ["update"] = new JsonObject
            {
                ["singleton"] = new JsonObject { ["subject/x"] = "no" },
                ["#good"] = new JsonObject { ["id"] = "other", ["subject"] = 10 },
                ["#date"] = new JsonObject { ["fromDate"] = "2026-09-29T12:34:56+01:00" },
                ["#valid"] = new JsonObject { ["subject"] = "Okay" },
            },
        };
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["good"] = "singleton",
            ["date"] = "singleton",
            ["valid"] = "singleton",
        };
        Assert.IsTrue(GatewayVacationSetCodec.TryParse(arguments, known, 4, out var call, out _));
        Assert.HasCount(1, call!.Command.Updates);
        var rendered = GatewayVacationSetCodec.Render(call,
            new MailVacationSetResult(MailVacationSetStatus.Ok, "s1", "s1", [new(false, ["textBody", "htmlBody"])]));
        var failures = rendered.Data["notUpdated"]!;
        Assert.AreEqual("invalidPatch", failures["singleton"]!["type"]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["#good"]!["type"]!.GetValue<string>());
        Assert.AreEqual("id", failures["#good"]!["properties"]![0]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["#date"]!["type"]!.GetValue<string>());
        Assert.AreEqual("fromDate", failures["#date"]!["properties"]![0]!.GetValue<string>());
        Assert.AreEqual("invalidProperties", failures["#valid"]!["type"]!.GetValue<string>());
        Assert.AreEqual("textBody", failures["#valid"]!["properties"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void GatewayRejectsMalformedMutationEnvelopeAndRetainsAccountErrorPrecedence()
    {
        var arguments = new JsonObject
        {
            ["accountId"] = "not-an-account",
            ["update"] = new JsonObject { ["singleton"] = new JsonObject { ["subject"] = "Away" } },
        };
        Assert.IsTrue(GatewayVacationSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 1, out var call, out _));
        Assert.AreEqual(Guid.Empty, call!.Command.AccountId);
        var accountError = GatewayVacationSetCodec.Render(call,
            new MailVacationSetResult(MailVacationSetStatus.AccountNotFound, null, null, []));
        Assert.AreEqual("accountNotFound", accountError.Data["type"]!.GetValue<string>());
        arguments["update"] = new JsonObject { ["#"] = new JsonObject() };
        Assert.IsFalse(GatewayVacationSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 1, out _, out var failure));
        Assert.AreEqual("invalidArguments", failure);
        arguments["update"] = new JsonObject { ["one"] = new JsonObject(), ["two"] = new JsonObject() };
        Assert.IsFalse(GatewayVacationSetCodec.TryParse(arguments,
            new Dictionary<string, string>(), 1, out _, out failure));
        Assert.AreEqual("requestTooLarge", failure);
    }

    [TestMethod]
    public async Task WorkerRejectsExtraInternalVacationFields()
    {
        await using var fixture = await JmapFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<JmapRequestProcessor>();
        var malformed = new MailOperationCommand([MailFeature.AutomaticReplies],
            MailOperationKind.MutateVacationSettings, new JsonObject
            {
                ["accountId"] = fixture.InboxId.ToString(),
                ["ifInState"] = null,
                ["updates"] = new JsonArray(),
                ["extra"] = true,
            }, new Dictionary<string, string>());
        var failure = await Assert.ThrowsAsync<MailApplicationException>(() =>
            processor.ExecuteAsync(malformed, fixture.User, null));
        Assert.AreEqual(MailFailureKind.MalformedBatch, failure.Failure.Kind);
    }
}
