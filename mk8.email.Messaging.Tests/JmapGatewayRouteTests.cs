using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Application.Worker;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Gateway.ApplicationBridge;
using mk8.email.Gateway.Protocols;
using mk8.email.Gateway.Protocols.Jmap;
using mk8.email.Jmap;
using Npgsql;

namespace mk8.email.Messaging.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("PostgreSQL")]
public sealed class JmapGatewayRouteTests
{
    [TestMethod]
    public async Task HttpApiRequestCrossesRemoteWorkerAndRecordsBothBoundaries()
    {
        await using var database = await RequirePostgresAsync();
        await using var gatewayDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var workerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMessagingSchema.EnsureAsync(gatewayDataSource);
        using var gatewayProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "jmap-route-key");
        using var workerProtector = AesGcmPayloadProtectorTests.CreateProtector(
            "test",
            "jmap-route-key");
        var messagingOptions = new PostgresMessagingOptions
        {
            NotificationFallbackInterval = TimeSpan.FromSeconds(1),
        };
        var gatewayBus = new PostgresApplicationBus(
            gatewayDataSource,
            gatewayProtector,
            messagingOptions);
        var workerBus = new PostgresApplicationBus(
            workerDataSource,
            workerProtector,
            messagingOptions);
        var journal = new PostgresGatewayTrafficJournal(
            gatewayDataSource,
            gatewayProtector,
            messagingOptions);
        var jmap = new StubJmapApplicationService();
        var workerServices = new ServiceCollection()
            .AddSingleton<IJmapApplicationService>(jmap)
            .AddScoped<IApplicationRequestDispatcher>(serviceProvider =>
                new ApplicationRequestDispatcher(serviceProvider));
        await using var workerProvider = workerServices.BuildServiceProvider();
        var worker = new ApplicationRequestWorker(
            workerBus,
            workerProvider.GetRequiredService<IServiceScopeFactory>(),
            new ApplicationWorkerIdentity("application@jmap-route-host", TimeSpan.FromSeconds(30)),
            NullLogger<ApplicationRequestWorker>.Instance);
        var environment = new EnvironmentConfig
        {
            Smtp = new SmtpConfig { Hostname = "email.example.test" },
            Jmap = new JmapConfig
            {
                EnableJmap = true,
                PublicBaseUrl = "https://email.example.test",
                MaxRequestSizeBytes = 65_536,
                MaxUploadSizeBytes = 1_048_576,
            },
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton<IApplicationRequestClient>(gatewayBus);
        builder.Services.AddSingleton<IGatewayTrafficJournal>(journal);
        builder.Services.AddGatewayApplicationClient();
        var application = builder.Build();
        application.UseMiddleware<GatewayProtocolTrafficCaptureMiddleware>();
        application.UseMiddleware<GatewayApplicationFailureMiddleware>();
        application.UseRouting();
        application.MapJmapEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await worker.StartAsync(timeout.Token);
        await application.StartAsync(timeout.Token);
        try
        {
            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new AssertFailedException("The JMAP Gateway did not publish an address.");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10),
            };
            const string requestDocument =
                """
                {"using":["urn:ietf:params:jmap:core","urn:ietf:params:jmap:mail",
                          "urn:ietf:params:jmap:submission","urn:ietf:params:jmap:contacts",
                          "urn:ietf:params:jmap:vacationresponse"],
                 "methodCalls":[["Mailbox/query",{"x":1,"X":2,"nested":{"items":[1,null,"text"],"key":3,"Key":4,"reference":"#made"}},"call-1"],
                   ["Core/echo",{"#copied":{"resultOf":"call-1","name":"Mailbox/query","path":"/nested/items/2"}},"call-2"],
                   ["Mailbox/query",{"#collision":{"resultOf":"call-1","name":"Mailbox/query","path":"/�~02"}},"call-3"],
                   ["Mailbox/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["M22222222222222222222222222222222","M33333333333333333333333333333333","M22222222222222222222222222222222"],
                     "properties":["name","myRights"]},"call-4"],
                   ["Mailbox/changes",{"accountId":"A11111111111111111111111111111111",
                     "sinceState":"s0","maxChanges":2},"call-5"],
                   ["Thread/changes",{"accountId":"A11111111111111111111111111111111","sinceState":"s0"},"call-6"],
                   ["Email/changes",{"accountId":"A11111111111111111111111111111111","sinceState":"s0"},"call-7"],
                   ["Identity/changes",{"accountId":"A11111111111111111111111111111111","sinceState":"s0"},"call-8"],
                   ["EmailSubmission/changes",{"accountId":"A11111111111111111111111111111111","sinceState":"s0"},"call-9"],
                   ["AddressBook/changes",{"accountId":"A11111111111111111111111111111111","sinceState":"s0"},"call-10"],
                   ["ContactCard/changes",{"accountId":"A11111111111111111111111111111111","sinceState":"s0"},"call-11"],
                   ["AddressBook/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["D22222222222222222222222222222222","D33333333333333333333333333333333"],
                     "properties":["name","myRights"]},"call-12"],
                   ["Identity/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["I22222222222222222222222222222222","I33333333333333333333333333333333"],
                     "properties":["email","replyTo"]},"call-13"],
                   ["VacationResponse/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["singleton","missing"],"properties":["isEnabled","fromDate","textBody"]},"call-14"],
                   ["PushSubscription/get",{"ids":["P22222222222222222222222222222222",
                     "P33333333333333333333333333333333"],
                     "properties":["deviceClientId","expires","types"]},"call-15"],
                   ["Thread/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["TYyE","Tmissing"],"properties":["emailIds"]},"call-16"],
                   ["EmailSubmission/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["S22222222222222222222222222222222","S33333333333333333333333333333333"],
                     "properties":["envelope","deliveryStatus","sendAt"]},"call-17"]],
                 "createdIds":{"made":"object-id"}}
                """;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/jmap/api")
            {
                Content = new StringContent(requestDocument, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("person@example.test:route-password-secret")));
            using var response = await client.SendAsync(request, timeout.Token);

            using var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, "/.well-known/jmap");
            discoveryRequest.Headers.Authorization = request.Headers.Authorization;
            using var discoveryResponse = await client.SendAsync(discoveryRequest, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);
            using var discovery = JsonDocument.Parse(await discoveryResponse.Content.ReadAsStringAsync(timeout.Token));

            using var eventRequest = new HttpRequestMessage(HttpMethod.Get, "/jmap/event?types=Mailbox&closeafter=state&ping=0");
            eventRequest.Headers.Authorization = request.Headers.Authorization;
            eventRequest.Headers.Add("Last-Event-ID", "c0");
            using var eventResponse = await client.SendAsync(eventRequest, timeout.Token);
            Assert.AreEqual(HttpStatusCode.OK, eventResponse.StatusCode);
            StringAssert.Contains(await eventResponse.Content.ReadAsStringAsync(timeout.Token),
                "data: {\"@type\":\"StateChange\",\"changed\":{\"account-id\":{\"Mailbox\":\"state-1\"}}}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.AreEqual(
                GatewayJmapProfileCodec.Render(StubJmapApplicationService.Profile, environment)["state"]!.GetValue<string>(),
                json.RootElement.GetProperty("sessionState").GetString());
            Assert.AreEqual(discovery.RootElement.GetProperty("state").GetString(), json.RootElement.GetProperty("sessionState").GetString());
            Assert.AreEqual("https://email.example.test/jmap/api", discovery.RootElement.GetProperty("apiUrl").GetString());
            Assert.AreEqual("person@example.test", jmap.Request?.Authentication.Username);
            Assert.AreEqual("route-password-secret", jmap.Request?.Authentication.Secret);
            Assert.IsNotNull(jmap.Request?.Command);
            CollectionAssert.AreEqual(new[] { MailFeature.Basic, MailFeature.Messages, MailFeature.Submission,
                MailFeature.Contacts, MailFeature.AutomaticReplies }, jmap.Request.Command.Features.ToArray());
            Assert.AreEqual(16, jmap.Executions);
            Assert.AreEqual("second", jmap.Commands[1].Arguments["collision"]!.GetValue<string>());
            Assert.IsFalse(jmap.Commands[1].Arguments.ContainsKey("#collision"));
            Assert.AreEqual(17, jmap.Plan?.Plan?.OperationCount);
            Assert.AreEqual(MailOperationKind.FindFolders, jmap.Request.Command.Operation);
            Assert.AreEqual("text", jmap.Request.Command.Arguments["nested"]?["items"]?[2]?.GetValue<string>());
            Assert.AreEqual(1, jmap.Request.Command.Arguments["x"]!.GetValue<int>());
            Assert.AreEqual(2, jmap.Request.Command.Arguments["X"]!.GetValue<int>());
            Assert.AreEqual(3, jmap.Request.Command.Arguments["nested"]!["key"]!.GetValue<int>());
            Assert.AreEqual(4, jmap.Request.Command.Arguments["nested"]!["Key"]!.GetValue<int>());
            Assert.AreEqual("#made", jmap.Request.Command.Arguments["nested"]!["reference"]!.GetValue<string>());
            Assert.AreEqual("made", jmap.Request.Command.ReferenceAliases["#made"]);
            Assert.AreEqual("object-id", jmap.Request.Command.KnownEntities?["made"]);
            var invocation = json.RootElement.GetProperty("methodResponses")[0];
            Assert.AreEqual("Mailbox/query", invocation[0].GetString());
            Assert.AreEqual("call-1", invocation[2].GetString());
            Assert.AreEqual(JsonValueKind.Null, invocation[1].GetProperty("nested").GetProperty("items")[1].ValueKind);
            Assert.AreEqual(1, invocation[1].GetProperty("x").GetInt32());
            Assert.AreEqual(2, invocation[1].GetProperty("X").GetInt32());
            Assert.AreEqual(3, invocation[1].GetProperty("nested").GetProperty("key").GetInt32());
            Assert.AreEqual(4, invocation[1].GetProperty("nested").GetProperty("Key").GetInt32());
            Assert.AreEqual("text", json.RootElement.GetProperty("methodResponses")[1][1].GetProperty("copied").GetString());
            Assert.AreEqual("call-2", json.RootElement.GetProperty("methodResponses")[1][2].GetString());
            Assert.AreEqual("object-id", json.RootElement.GetProperty("createdIds").GetProperty("made").GetString());
            var folderCommand = jmap.Commands[2];
            Assert.AreEqual(MailOperationKind.ReadFolders, folderCommand.Operation);
            Assert.IsFalse(folderCommand.Arguments.ContainsKey("ids"));
            Assert.IsFalse(folderCommand.Arguments.ContainsKey("properties"));
            Assert.AreEqual("11111111-1111-1111-1111-111111111111", folderCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual(3, folderCommand.Arguments["folderIds"]!.AsArray().Count);
            var folderResponse = json.RootElement.GetProperty("methodResponses")[3];
            Assert.AreEqual("Mailbox/get", folderResponse[0].GetString());
            Assert.AreEqual("call-4", folderResponse[2].GetString());
            var folderData = folderResponse[1];
            Assert.AreEqual("A11111111111111111111111111111111", folderData.GetProperty("accountId").GetString());
            Assert.AreEqual("s42", folderData.GetProperty("state").GetString());
            Assert.AreEqual(1, folderData.GetProperty("list").GetArrayLength());
            Assert.AreEqual("Inbox", folderData.GetProperty("list")[0].GetProperty("name").GetString());
            Assert.IsFalse(folderData.GetProperty("list")[0].GetProperty("myRights").GetProperty("mayDelete").GetBoolean());
            Assert.IsFalse(folderData.GetProperty("list")[0].TryGetProperty("totalEmails", out _));
            Assert.AreEqual("M33333333333333333333333333333333", folderData.GetProperty("notFound")[0].GetString());
            var changesCommand = jmap.Commands[3];
            Assert.AreEqual(MailOperationKind.ReadFolderChanges, changesCommand.Operation);
            Assert.AreEqual(2L, changesCommand.Arguments["maxChanges"]!.GetValue<long>());
            Assert.AreEqual("s0", changesCommand.Arguments["sinceState"]!.GetValue<string>());
            var changesResponse = json.RootElement.GetProperty("methodResponses")[4];
            Assert.AreEqual("Mailbox/changes", changesResponse[0].GetString());
            Assert.AreEqual("call-5", changesResponse[2].GetString());
            Assert.AreEqual("s0", changesResponse[1].GetProperty("oldState").GetString());
            Assert.AreEqual("s42", changesResponse[1].GetProperty("newState").GetString());
            Assert.AreEqual("M22222222222222222222222222222222",
                changesResponse[1].GetProperty("created")[0].GetString());
            var changeOperations = new[]
            {
                MailOperationKind.ReadThreadChanges, MailOperationKind.ReadMessageChanges,
                MailOperationKind.ReadSenderIdentityChanges, MailOperationKind.ReadSubmissionChanges,
                MailOperationKind.ReadAddressBookChanges, MailOperationKind.ReadContactChanges,
            };
            var changeNames = new[]
            {
                "Thread/changes", "Email/changes", "Identity/changes",
                "EmailSubmission/changes", "AddressBook/changes", "ContactCard/changes",
            };
            for (var index = 0; index < changeOperations.Length; index++)
            {
                Assert.AreEqual(changeOperations[index], jmap.Commands[index + 4].Operation);
                Assert.IsTrue(jmap.Commands[index + 4].Arguments.ContainsKey("sinceState"));
                var displayed = json.RootElement.GetProperty("methodResponses")[index + 5];
                Assert.AreEqual(changeNames[index], displayed[0].GetString());
                Assert.AreEqual($"call-{index + 6}", displayed[2].GetString());
                Assert.AreEqual("s42", displayed[1].GetProperty("newState").GetString());
                Assert.AreEqual(1, displayed[1].GetProperty("created").GetArrayLength());
                Assert.AreEqual(changeOperations[index] == MailOperationKind.ReadContactChanges,
                    displayed[1].TryGetProperty("updatedProperties", out _));
            }
            var bookCommand = jmap.Commands[10];
            Assert.AreEqual(MailOperationKind.ReadAddressBooks, bookCommand.Operation);
            Assert.IsFalse(bookCommand.Arguments.ContainsKey("ids"));
            Assert.IsFalse(bookCommand.Arguments.ContainsKey("properties"));
            Assert.AreEqual(2, bookCommand.Arguments["bookIds"]!.AsArray().Count);
            var bookResponse = json.RootElement.GetProperty("methodResponses")[11];
            Assert.AreEqual("AddressBook/get", bookResponse[0].GetString());
            Assert.AreEqual("call-12", bookResponse[2].GetString());
            Assert.AreEqual("Personal", bookResponse[1].GetProperty("list")[0].GetProperty("name").GetString());
            Assert.IsFalse(bookResponse[1].GetProperty("list")[0].GetProperty("myRights")
                .GetProperty("mayDelete").GetBoolean());
            Assert.AreEqual("D33333333333333333333333333333333",
                bookResponse[1].GetProperty("notFound")[0].GetString());
            var identityCommand = jmap.Commands[11];
            Assert.AreEqual(MailOperationKind.ReadSenderIdentities, identityCommand.Operation);
            Assert.IsFalse(identityCommand.Arguments.ContainsKey("ids"));
            Assert.IsFalse(identityCommand.Arguments.ContainsKey("properties"));
            Assert.AreEqual(2, identityCommand.Arguments["identityIds"]!.AsArray().Count);
            var identityResponse = json.RootElement.GetProperty("methodResponses")[12];
            Assert.AreEqual("Identity/get", identityResponse[0].GetString());
            Assert.AreEqual("call-13", identityResponse[2].GetString());
            Assert.AreEqual("sender@example.test",
                identityResponse[1].GetProperty("list")[0].GetProperty("email").GetString());
            Assert.AreEqual("reply@example.test",
                identityResponse[1].GetProperty("list")[0].GetProperty("replyTo")[0].GetProperty("email").GetString());
            Assert.IsFalse(identityResponse[1].GetProperty("list")[0].TryGetProperty("name", out _));
            Assert.AreEqual("I33333333333333333333333333333333",
                identityResponse[1].GetProperty("notFound")[0].GetString());
            var vacationCommand = jmap.Commands[12];
            Assert.AreEqual(MailOperationKind.ReadVacationSettings, vacationCommand.Operation);
            Assert.IsTrue(vacationCommand.Arguments["includeSingleton"]!.GetValue<bool>());
            Assert.IsTrue(vacationCommand.Arguments["includeBodies"]!.GetValue<bool>());
            var vacationResponse = json.RootElement.GetProperty("methodResponses")[13];
            Assert.AreEqual("VacationResponse/get", vacationResponse[0].GetString());
            Assert.AreEqual("call-14", vacationResponse[2].GetString());
            Assert.AreEqual("2026-09-29T12:34:56Z",
                vacationResponse[1].GetProperty("list")[0].GetProperty("fromDate").GetString());
            Assert.AreEqual("Away", vacationResponse[1].GetProperty("list")[0].GetProperty("textBody").GetString());
            Assert.AreEqual("missing", vacationResponse[1].GetProperty("notFound")[0].GetString());
            var pushCommand = jmap.Commands[13];
            Assert.AreEqual(MailOperationKind.ReadNotificationSubscriptions, pushCommand.Operation);
            Assert.AreEqual(2, pushCommand.Arguments["subscriptionIds"]!.AsArray().Count);
            var pushResponse = json.RootElement.GetProperty("methodResponses")[14];
            Assert.AreEqual("PushSubscription/get", pushResponse[0].GetString());
            Assert.AreEqual("call-15", pushResponse[2].GetString());
            Assert.AreEqual("device", pushResponse[1].GetProperty("list")[0]
                .GetProperty("deviceClientId").GetString());
            Assert.AreEqual("Mailbox", pushResponse[1].GetProperty("list")[0]
                .GetProperty("types")[0].GetString());
            Assert.AreEqual("P33333333333333333333333333333333",
                pushResponse[1].GetProperty("notFound")[0].GetString());
            var threadCommand = jmap.Commands[14];
            Assert.AreEqual(MailOperationKind.ReadThreads, threadCommand.Operation);
            Assert.AreEqual(1, threadCommand.Arguments.Count);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                threadCommand.Arguments["accountId"]!.GetValue<string>());
            var threadResponse = json.RootElement.GetProperty("methodResponses")[15];
            Assert.AreEqual("Thread/get", threadResponse[0].GetString());
            Assert.AreEqual("call-16", threadResponse[2].GetString());
            Assert.AreEqual("TYyE", threadResponse[1].GetProperty("list")[0]
                .GetProperty("id").GetString());
            Assert.AreEqual("E22222222222222222222222222222222",
                threadResponse[1].GetProperty("list")[0].GetProperty("emailIds")[0].GetString());
            Assert.AreEqual("Tmissing", threadResponse[1].GetProperty("notFound")[0].GetString());
            var submissionCommand = jmap.Commands[15];
            Assert.AreEqual(MailOperationKind.ReadSubmissions, submissionCommand.Operation);
            Assert.IsFalse(submissionCommand.Arguments.ContainsKey("ids"));
            Assert.IsFalse(submissionCommand.Arguments.ContainsKey("properties"));
            Assert.AreEqual(2, submissionCommand.Arguments["submissionIds"]!.AsArray().Count);
            Assert.IsTrue(submissionCommand.Arguments["includeDeliveryStatus"]!.GetValue<bool>());
            var submissionResponse = json.RootElement.GetProperty("methodResponses")[16];
            Assert.AreEqual("EmailSubmission/get", submissionResponse[0].GetString());
            Assert.AreEqual("call-17", submissionResponse[2].GetString());
            Assert.AreEqual("12", submissionResponse[1].GetProperty("list")[0]
                .GetProperty("envelope").GetProperty("mailFrom").GetProperty("parameters")
                .GetProperty("SIZE").GetString());
            Assert.AreEqual("queued", submissionResponse[1].GetProperty("list")[0]
                .GetProperty("deliveryStatus").GetProperty("recipient@example.test")
                .GetProperty("delivered").GetString());
            Assert.AreEqual("S33333333333333333333333333333333",
                submissionResponse[1].GetProperty("notFound")[0].GetString());

            await using var countCommand = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE metadata ->> 'layer' = 'presentation') "
                + "FROM gateway_traffic_records WHERE protocol = 'jmap'");
            await using var countReader = await countCommand.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await countReader.ReadAsync(timeout.Token));
            // SSE records its headers and streamed body separately, in addition
            // to the request and two application-boundary records.
            Assert.AreEqual(45L, countReader.GetInt64(0));
            Assert.AreEqual(7L, countReader.GetInt64(1));

            await using var operationCommand = gatewayDataSource.CreateCommand(
                "SELECT operation FROM application_requests WHERE operation = @operation LIMIT 1");
            operationCommand.Parameters.AddWithValue("operation", ApplicationOperations.MailOperationExecute);
            Assert.AreEqual(
                ApplicationOperations.MailOperationExecute,
                await operationCommand.ExecuteScalarAsync(timeout.Token));
            await using var ciphertextCommand = gatewayDataSource.CreateCommand(
                "SELECT payload_inline FROM gateway_traffic_records WHERE payload_inline IS NOT NULL");
            await using var ciphertextReader = await ciphertextCommand.ExecuteReaderAsync(timeout.Token);
            while (await ciphertextReader.ReadAsync(timeout.Token))
            {
                var ciphertext = ciphertextReader.GetFieldValue<byte[]>(0);
                Assert.IsFalse(
                    Encoding.UTF8.GetString(ciphertext)
                        .Contains("route-password-secret", StringComparison.Ordinal));
            }
        }
        finally
        {
            await application.StopAsync(timeout.Token);
            await application.DisposeAsync();
            await worker.StopAsync(timeout.Token);
            worker.Dispose();
        }
    }

    private static async Task<PostgresTestDatabase> RequirePostgresAsync()
    {
        var database = await PostgresTestDatabase.TryCreateAsync();
        if (database is null)
        {
            Assert.Inconclusive(
                "Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            throw new InvalidOperationException("PostgreSQL integration test configuration is required.");
        }
        return database;
    }

    private sealed class StubJmapApplicationService : IJmapApplicationService
    {
        internal static JmapApplicationProfile Profile { get; } = new("remote-worker",
            new JmapServiceLimits(10000, 1, 10000, 1, 64, 500, 500, 32, 255, 10000,
                ["i;ascii-numeric"], ["receivedAt"]), []);

        public MailOperationApplicationRequest? Request { get; private set; }

        public Task<JmapApplicationResult> GetProfileAsync(
            JmapProfileApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok, Profile: Profile));

        public MailPlanApplicationRequest? Plan { get; private set; }
        public int Executions { get; private set; }
        public List<MailOperationCommand> Commands { get; } = [];

        public Task<JmapApplicationResult> ValidatePlanAsync(MailPlanApplicationRequest request, CancellationToken cancellationToken = default)
        {
            Plan = request;
            return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok, Profile: Profile));
        }

        public Task<JmapApplicationResult> ExecuteOperationAsync(
            MailOperationApplicationRequest request, Guid operationId, CancellationToken cancellationToken = default)
        {
            Request ??= request;
            Executions++;
            Commands.Add(request.Command);
            if (request.Command.Operation == MailOperationKind.ReadFolders)
            {
                var read = new MailFolderReadResult(MailFolderReadStatus.Ok, "s42",
                    [new MailFolderSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "Inbox", null, "inbox", 0, true, 2, 1, 2, 1, true)]);
                var node = JsonSerializer.SerializeToNode(read, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadFolders, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (MailChangeOperations.TryGetFeature(request.Command.Operation, out _))
            {
                var objectId = request.Command.Operation switch
                {
                    MailOperationKind.ReadFolderChanges => "M22222222222222222222222222222222",
                    MailOperationKind.ReadThreadChanges => "T22222222222222222222222222222222",
                    MailOperationKind.ReadMessageChanges => "E22222222222222222222222222222222",
                    MailOperationKind.ReadSenderIdentityChanges => "I22222222222222222222222222222222",
                    MailOperationKind.ReadSubmissionChanges => "S22222222222222222222222222222222",
                    MailOperationKind.ReadAddressBookChanges => "D22222222222222222222222222222222",
                    MailOperationKind.ReadContactChanges => "C22222222222222222222222222222222",
                    _ => throw new InvalidOperationException(),
                };
                var changes = new MailChangesResult(MailChangesStatus.Ok, "s0", "s42", false,
                    [objectId], [], []);
                var node = JsonSerializer.SerializeToNode(changes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(request.Command.Operation, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadAddressBooks)
            {
                var books = new MailAddressBookReadResult(MailAddressBookReadStatus.Ok, "s43",
                    [new MailAddressBookSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "Personal", "private", 0, true, true, true)]);
                var node = JsonSerializer.SerializeToNode(books, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadAddressBooks, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadSenderIdentities)
            {
                var identities = new MailIdentityReadResult(MailIdentityReadStatus.Ok, "s44",
                    [new MailIdentitySnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "Sender", "sender@example.test",
                        new MailIdentityAddressListSnapshot(
                            [new MailIdentityAddressSnapshot("Reply", "reply@example.test")]), null,
                        "", "", true)]);
                var node = JsonSerializer.SerializeToNode(identities, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadSenderIdentities, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadVacationSettings)
            {
                var vacation = new MailVacationReadResult(MailVacationReadStatus.Ok, "s45",
                    new MailVacationSnapshot(true,
                        new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc),
                        null, "Away", "Away", null));
                var node = JsonSerializer.SerializeToNode(vacation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadVacationSettings, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadNotificationSubscriptions)
            {
                var subscriptions = new MailPushSubscriptionReadResult(MailPushSubscriptionReadStatus.Ok,
                    [new MailPushSubscriptionSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "device", null, new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc),
                        ["Mailbox"])]);
                var node = JsonSerializer.SerializeToNode(subscriptions, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadNotificationSubscriptions, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadThreads)
            {
                var threads = new MailThreadReadResult(MailThreadReadStatus.Ok, "s46",
                    [new MailThreadEmailSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "c!")]);
                var node = JsonSerializer.SerializeToNode(threads, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadThreads, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadSubmissions)
            {
                var submissions = new MailSubmissionReadResult(MailSubmissionReadStatus.Ok, "s47",
                    [new MailSubmissionSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "I11111111111111111111111111111111", "E11111111111111111111111111111111",
                        "TYyE", "{\"mailFrom\":{\"email\":\"sender@example.test\",\"parameters\":{\"SIZE\":\"12\"}},\"rcptTo\":[]}",
                        "sender@example.test", ["recipient@example.test"],
                        new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc), "final",
                        [new MailSubmissionDeliverySnapshot("recipient@example.test",
                            MailSubmissionDeliveryState.Pending, null)])]);
                var node = JsonSerializer.SerializeToNode(submissions, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadSubmissions, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            var data = (System.Text.Json.Nodes.JsonObject)request.Command.Arguments.DeepClone();
            if (Executions == 1)
            {
                data["\ud800"] = "first";
                data["\udfff"] = "second";
            }
            return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                OperationResult: new(new(request.Command.Operation, ApplicationValueCodec.Encode(data)),
                    request.Command.KnownEntities!, Profile)));
        }

        public Task<JmapApplicationResult> UploadAsync(
            JmapUploadApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> DownloadAsync(
            JmapDownloadApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JmapApplicationResult> PollChangesAsync(
            JmapChangesApplicationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok, Cursor: 42,
                Changes: new JmapApplicationChanges(new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["account-id"] = new Dictionary<string, string> { ["Mailbox"] = "state-1" },
                })));
    }
}
