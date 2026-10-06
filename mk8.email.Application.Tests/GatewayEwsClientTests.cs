using System.Text.Json;
using System.Text.Json.Nodes;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols.Ews;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals executes this typed-result validation fixture; discovery is retained.")]
internal sealed class GatewayEwsClientTests
{
    private static readonly Guid Account = new("1138f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid Folder = new("1338f53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly ProtocolAuthentication Authentication = new(ProtocolAuthenticationKinds.Password, "owner@example.test", "test-only-secret");
    private static readonly JmapApplicationProfile Profile = new("owner@example.test",
        new(1024, 1, 65_536, 8, 64, 500, 500, 50, 100, 1024, [], []), [new($"A{Account:N}", "owner@example.test", true, false, true)]);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ExistingTypedContractsCarryEwsTrafficWithoutRawSoapOrNewOperation()
    {
        var transport = new Transport([new(JmapApplicationOutcomes.Ok, Profile: Profile), Query(1), Read()]);
        var client = Client(transport);
        var profile = await client.AuthenticateAsync(Authentication, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(profile);
        var graph = await client.ReadGraphAsync(Authentication, profile, Account, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailFolderReadStatus.Ok, graph.Status);
        Assert.HasCount(1, graph.Folders);
        CollectionAssert.AreEqual(new[] { ApplicationOperations.JmapProfileGet, ApplicationOperations.MailOperationExecute, ApplicationOperations.MailOperationExecute }, transport.Operations);
    }

    [TestMethod]
    [DataRow(501)]
    [DataRow(10_000)]
    public async Task OversizedCountNeverRequestsAFullEncodedSnapshot(int count)
    {
        var transport = new Transport([Query(count)]);
        var result = await Client(transport).ReadGraphAsync(Authentication, Profile, Account, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailFolderReadStatus.RequestTooLarge, result.Status);
        Assert.HasCount(0, result.Folders);
        Assert.HasCount(1, transport.Operations);
    }

    [TestMethod]
    public async Task CountAndSnapshotStateDisagreementIsBusyInsteadOfAnInconsistentPage()
    {
        var transport = new Transport([Query(1), Read(state: "changed")]);
        var error = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => Client(transport)
            .ReadGraphAsync(Authentication, Profile, Account, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("ErrorServerBusy", error.Code, StringComparer.Ordinal);
        Assert.AreEqual(503, error.Status);
    }

    [TestMethod]
    [DataRow("missing-counter")]
    [DataRow("duplicate-id")]
    [DataRow("cycle")]
    [DataRow("negative")]
    [DataRow("wrong-operation")]
    [DataRow("wrong-profile")]
    public async Task MalformedTypedWorkerRepliesFailClosed(string mode)
    {
        var read = Read();
        var response = read.OperationResult!.Response;
        var data = (JsonObject)ApplicationValueCodec.Decode(response.Data)!;
        var folders = (JsonArray)data["folders"]!;
        if (mode is "missing-counter") ((JsonObject)folders[0]!).Remove("totalEmails");
        if (mode is "duplicate-id") folders.Add(folders[0]!.DeepClone());
        if (mode is "cycle") folders[0]!["parentId"] = Folder;
        if (mode is "negative") folders[0]!["totalEmails"] = -1;
        read = read with
        {
            OperationResult = read.OperationResult with
            {
                Response = response with
                {
                    Data = ApplicationValueCodec.Encode(data),
                    Operation = mode is "wrong-operation" ? MailOperationKind.FindFolders : MailOperationKind.ReadFolders,
                },
                Profile = mode is "wrong-profile" ? Profile with { Username = "foreign@example.test" } : Profile
            }
        };
        var transport = new Transport([Query(mode is "duplicate-id" ? 2 : 1), read]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(transport).ReadGraphAsync(Authentication, Profile, Account, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LostAccountAuthorityOrCredentialsDoesNotExposeASnapshot()
    {
        var denied = Reply(MailOperationKind.FindFolders, new MailFolderQueryResult(MailFolderQueryStatus.AccountNotFound, null, 0, [], 0));
        var deniedTransport = new Transport([denied]);
        var result = await Client(deniedTransport).ReadGraphAsync(Authentication, Profile, Account, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailFolderReadStatus.AccountNotFound, result.Status);
        Assert.HasCount(1, deniedTransport.Operations);
        var expired = new Transport([new(JmapApplicationOutcomes.Unauthorized)]);
        var exception = await Assert.ThrowsAsync<GatewayEwsRequestException>(() => Client(expired).ReadGraphAsync(Authentication, Profile, Account, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(401, exception.Status);
    }

    [TestMethod]
    public async Task SmallEnvelopeCapacityRefusesBeforeRequestingTheSnapshot()
    {
        const int payloadBytes = 1_572_864;
        var capacity = GatewayEwsClient.MaximumFolders(Profile, payloadBytes);
        Assert.AreEqual(319, capacity);
        var transport = new Transport([Query(capacity + 1)]);
        var result = await new GatewayEwsClient(transport, new EnvironmentConfig
        {
            Messaging = new MessagingConfig { MaxPayloadBytes = payloadBytes },
        }).ReadGraphAsync(Authentication, Profile, Account, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MailFolderReadStatus.RequestTooLarge, result.Status);
        Assert.HasCount(1, transport.Operations);
    }

    [TestMethod]
    public void ConservativePerFolderBudgetCoversWorstCaseEncodedDomainValues()
    {
        var folder = new MailFolderSnapshot(Folder, new string('`', 255), Account, new string('`', 32),
            long.MinValue, true, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, true);
        var result = Reply(MailOperationKind.ReadFolders, new MailFolderReadResult(MailFolderReadStatus.Ok, new string('`', 256), [folder]));
        var encodedBytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length;
        var empty = Reply(MailOperationKind.ReadFolders, new MailFolderReadResult(MailFolderReadStatus.Ok, new string('`', 256), []));
        var fixedBytes = JsonSerializer.SerializeToUtf8Bytes(empty, JsonOptions).Length;
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes(Profile, JsonOptions).Length;
        Assert.IsLessThan(GatewayEwsClient.EncodedFolderBudgetBytes, encodedBytes - fixedBytes);
        Assert.IsLessThan(GatewayHttpPayloadBudget.MetadataBytes + profileBytes, fixedBytes);
        Assert.AreEqual(500, GatewayEwsClient.MaximumFolders(Profile, 64 * 1024 * 1024));
        Assert.AreEqual(0, GatewayEwsClient.MaximumFolders(Profile, GatewayHttpPayloadBudget.MetadataBytes));
    }

    private static GatewayEwsClient Client(Transport transport) => new(transport, new EnvironmentConfig());

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-field")]
    [DataRow("missing-status")]
    [DataRow("unknown-field")]
    [DataRow("wrong-token")]
    [DataRow("wrong-name")]
    [DataRow("wrong-parent")]
    [DataRow("empty-id")]
    [DataRow("wrong-state")]
    [DataRow("known-entity")]
    [DataRow("missing-map")]
    [DataRow("both-outcomes")]
    [DataRow("null-update")]
    public void MutationRepliesRequireCompleteCorrelatedTypedOutcomes(string mode)
    {
        var command = new MailFolderMutationCommand(Account, "state", true, [new("ews0", new("New", null, null, 0, false), null)], [], []);
        var result = new MailFolderMutationResult(MailFolderMutationStatus.Ok, "state", "newState",
            [new("ews0", new(Folder, "New", null, null, 0, false), null)], [], []);
        var data = (JsonObject)JsonSerializer.SerializeToNode(result, JsonOptions)!;
        var item = (JsonObject)data["created"]![0]!;
        if (mode is "missing-field") ((JsonObject)item["folder"]!).Remove("sortOrder");
        if (mode is "missing-status") data.Remove("status");
        if (mode is "unknown-field") item["untrusted"] = true;
        if (mode is "wrong-token") item["creationId"] = "other";
        if (mode is "wrong-name") item["folder"]!["name"] = "Other";
        if (mode is "wrong-parent") item["folder"]!["parentId"] = Account;
        if (mode is "empty-id") item["folder"]!["id"] = Guid.Empty;
        if (mode is "wrong-state") data["oldState"] = "other";
        if (mode is "both-outcomes") item["failure"] = JsonSerializer.SerializeToNode(new MailFolderMutationFailure(MailFolderMutationError.Forbidden), JsonOptions);
        if (mode is "null-update") ((JsonArray)data["updated"]!).Add((JsonNode?)null);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode is not "missing-map") known.Add("ews0", $"M{(mode is "known-entity" ? Account : Folder):N}");
        var reply = new MailOperationResult(new(MailOperationKind.MutateFolders, ApplicationValueCodec.Encode(data)), known, Profile);
        if (mode is "valid") Assert.AreEqual(Folder, GatewayEwsMutationReply.Decode(reply, command).Created[0].Folder!.Id);
        else Assert.Throws<InvalidOperationException>(() => GatewayEwsMutationReply.Decode(reply, command));
    }

    private static JmapApplicationResult Query(int total) => Reply(MailOperationKind.FindFolders,
        new MailFolderQueryResult(MailFolderQueryStatus.Ok, "state", 0, [], total));

    private static JmapApplicationResult Read(string state = "state") => Reply(MailOperationKind.ReadFolders,
        new MailFolderReadResult(MailFolderReadStatus.Ok, state, [new(Folder, "INBOX", null, "inbox", 0, true, 2, 1, 2, 1, true)]));

    private static JmapApplicationResult Reply<T>(MailOperationKind operation, T value) => new(JmapApplicationOutcomes.Ok,
        OperationResult: new(new(operation, ApplicationValueCodec.Encode(JsonSerializer.SerializeToNode(value, JsonOptions))),
            new Dictionary<string, string>(StringComparer.Ordinal), Profile));

    private sealed class Transport(IEnumerable<JmapApplicationResult> results) : IGatewayApplicationTransport
    {
        private readonly Queue<JmapApplicationResult> _results = new(results);
        public List<string> Operations { get; } = [];

        public Task<TResponse> SendAsync<TRequest, TResponse>(string protocol, string operation, TRequest request, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("ews", protocol, StringComparer.Ordinal);
            Assert.IsTrue(request is JmapProfileApplicationRequest or MailOperationApplicationRequest);
            if (request is MailOperationApplicationRequest mail)
            {
                Assert.IsTrue(mail.Command.Operation is MailOperationKind.FindFolders or MailOperationKind.ReadFolders);
                Assert.AreEqual(Account, mail.Command.Arguments["accountId"]!.GetValue<Guid>());
            }
            Operations.Add(operation);
            return Task.FromResult((TResponse)(object)_results.Dequeue());
        }
    }
}
