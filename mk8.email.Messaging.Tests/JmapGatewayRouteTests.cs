using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                 "methodCalls":[["Email/get",{"x":1,"X":2,"nested":{"items":[1,null,"text"],"key":3,"Key":4,"reference":"#made"}},"call-1"],
                   ["Core/echo",{"#copied":{"resultOf":"call-1","name":"Email/get","path":"/nested/items/2"}},"call-2"],
                   ["Email/get",{"#collision":{"resultOf":"call-1","name":"Email/get","path":"/�~02"}},"call-3"],
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
                     "properties":["envelope","deliveryStatus","sendAt"]},"call-17"],
                   ["Blob/copy",{"fromAccountId":"A11111111111111111111111111111111",
                     "accountId":"A22222222222222222222222222222222",
                     "blobIds":["U44444444444444444444444444444444",
                       "U66666666666666666666666666666666"]},"call-18"],
                   ["VacationResponse/set",{"accountId":"A11111111111111111111111111111111",
                     "ifInState":"s48","update":{"singleton":{"isEnabled":true,
                       "fromDate":"2026-09-29T12:34:56Z","textBody":"Away"},
                       "missing":{"subject":"ignored"}},"create":{"new":{}},
                     "destroy":["singleton"]},"call-19"],
                   ["EmailSubmission/query",{"accountId":"A11111111111111111111111111111111",
                     "filter":{"undoStatus":"final"},
                     "sort":[{"property":"sentAt","isAscending":true}],
                     "limit":1,"calculateTotal":true},"call-20"],
                   ["EmailSubmission/queryChanges",{"accountId":"A11111111111111111111111111111111",
                     "sinceQueryState":"s50","maxChanges":2,"calculateTotal":true},"call-21"],
                   ["Mailbox/query",{"accountId":"A11111111111111111111111111111111",
                     "filter":{"role":"inbox"},"sort":[{"property":"name","collation":"i;ascii-numeric"}],
                     "filterAsTree":true,"limit":1,"calculateTotal":true},"call-22"],
                   ["Mailbox/queryChanges",{"accountId":"A11111111111111111111111111111111",
                     "sinceQueryState":"s52","maxChanges":2,"calculateTotal":true},"call-23"],
                   ["ContactCard/copy",{"fromAccountId":"A11111111111111111111111111111111",
                     "accountId":"A22222222222222222222222222222222",
                     "create":{"copy":{"id":"C33333333333333333333333333333333"}},
                     "onSuccessDestroyOriginal":true},"call-24"],
                   ["ContactCard/query",{"accountId":"A11111111111111111111111111111111",
                     "filter":{"email":"person@example.test"},
                     "sort":[{"property":"name/surname","collation":"i;ascii-casemap"}],
                     "limit":1,"calculateTotal":true},"call-25"],
                   ["ContactCard/queryChanges",{"accountId":"A11111111111111111111111111111111",
                     "sinceQueryState":"s55","maxChanges":2,"calculateTotal":true},"call-26"],
                   ["ContactCard/get",{"accountId":"A11111111111111111111111111111111",
                     "ids":["C22222222222222222222222222222222","C33333333333333333333333333333333"],
                     "properties":["uid","name","addressBookIds"]},"call-27"],
                   ["Email/import",{"accountId":"A11111111111111111111111111111111",
                     "emails":{"draft":{"blobId":"U44444444444444444444444444444444",
                       "mailboxIds":{"M33333333333333333333333333333333":true},
                       "keywords":{"$seen":true},"receivedAt":"2026-09-29T12:34:56Z"}}},"call-28"],
                   ["Email/copy",{"fromAccountId":"A11111111111111111111111111111111",
                     "accountId":"A22222222222222222222222222222222",
                     "create":{"copied":{"id":"E11111111111111111111111111111111",
                       "mailboxIds":{"M33333333333333333333333333333333":true}}},
                     "onSuccessDestroyOriginal":true},"call-29"],
                   ["Email/query",{"accountId":"A11111111111111111111111111111111",
                     "filter":{"subject":"needle","hasKeyword":"$seen"},
                     "sort":[{"property":"subject","collation":"i;ascii-numeric"}],
                     "limit":1,"calculateTotal":true},"call-30"],
                   ["Email/queryChanges",{"accountId":"A11111111111111111111111111111111",
                     "filter":{"subject":"needle"},"sinceQueryState":"s62",
                     "maxChanges":2,"calculateTotal":true},"call-31"],
                   ["SearchSnippet/get",{"accountId":"A11111111111111111111111111111111",
                     "filter":{"subject":"needle"},
                     "emailIds":["E22222222222222222222222222222222","missing"]},"call-32"],
                   ["Identity/set",{"accountId":"A11111111111111111111111111111111",
                     "create":{"route-identity":{"email":"person@example.test"}}},"call-33"],
                   ["AddressBook/set",{"accountId":"A11111111111111111111111111111111",
                     "create":{"route-book":{"name":"Route Book"}},
                     "onSuccessSetIsDefault":"#route-book"},"call-34"],
                   ["PushSubscription/set",{"create":{"route-push":{
                     "deviceClientId":"route-device","url":"https://1.1.1.1/push"}}},"call-35"],
                   ["ContactCard/set",{"accountId":"A11111111111111111111111111111111",
                     "create":{"route-card":{"@type":"Card","version":"1.0","uid":"route-uid",
                       "addressBookIds":{"#route-book":true}}}},"call-36"],
                   ["Mailbox/set",{"accountId":"A11111111111111111111111111111111",
                     "create":{"route-folder":{"name":"Route Folder"}}},"call-37"]],
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
            Assert.AreEqual(36, jmap.Executions);
            Assert.AreEqual("second", jmap.Commands[1].Arguments["collision"]!.GetValue<string>());
            Assert.IsFalse(jmap.Commands[1].Arguments.ContainsKey("#collision"));
            Assert.AreEqual(37, jmap.Plan?.Plan?.OperationCount);
            Assert.AreEqual(MailOperationKind.ReadMessages, jmap.Request.Command.Operation);
            Assert.AreEqual("text", jmap.Request.Command.Arguments["nested"]?["items"]?[2]?.GetValue<string>());
            Assert.AreEqual(1, jmap.Request.Command.Arguments["x"]!.GetValue<int>());
            Assert.AreEqual(2, jmap.Request.Command.Arguments["X"]!.GetValue<int>());
            Assert.AreEqual(3, jmap.Request.Command.Arguments["nested"]!["key"]!.GetValue<int>());
            Assert.AreEqual(4, jmap.Request.Command.Arguments["nested"]!["Key"]!.GetValue<int>());
            Assert.AreEqual("#made", jmap.Request.Command.Arguments["nested"]!["reference"]!.GetValue<string>());
            Assert.AreEqual("made", jmap.Request.Command.ReferenceAliases["#made"]);
            Assert.AreEqual("object-id", jmap.Request.Command.KnownEntities?["made"]);
            var invocation = json.RootElement.GetProperty("methodResponses")[0];
            Assert.AreEqual("Email/get", invocation[0].GetString());
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
            var copyCommand = jmap.Commands[16];
            Assert.AreEqual(MailOperationKind.CopyBinaryObjects, copyCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                copyCommand.Arguments["fromAccountId"]!.GetValue<string>());
            Assert.AreEqual("22222222-2222-2222-2222-222222222222",
                copyCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual(2, copyCommand.Arguments["blobIds"]!.AsArray().Count);
            var copyResponse = json.RootElement.GetProperty("methodResponses")[17];
            Assert.AreEqual("Blob/copy", copyResponse[0].GetString());
            Assert.AreEqual("call-18", copyResponse[2].GetString());
            Assert.AreEqual("U55555555555555555555555555555555",
                copyResponse[1].GetProperty("copied").GetProperty("U44444444444444444444444444444444")
                    .GetString());
            Assert.AreEqual("notFound", copyResponse[1].GetProperty("notCopied")
                .GetProperty("U66666666666666666666666666666666").GetProperty("type").GetString());
            var vacationSetCommand = jmap.Commands[17];
            Assert.AreEqual(MailOperationKind.MutateVacationSettings, vacationSetCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                vacationSetCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual("s48", vacationSetCommand.Arguments["ifInState"]!.GetValue<string>());
            Assert.AreEqual(1, vacationSetCommand.Arguments["updates"]!.AsArray().Count);
            Assert.IsTrue(vacationSetCommand.Arguments["updates"]![0]!["setTextBody"]!.GetValue<bool>());
            Assert.IsFalse(vacationSetCommand.Arguments.ContainsKey("create"));
            Assert.IsFalse(vacationSetCommand.Arguments.ContainsKey("destroy"));
            var vacationSetResponse = json.RootElement.GetProperty("methodResponses")[18];
            Assert.AreEqual("VacationResponse/set", vacationSetResponse[0].GetString());
            Assert.AreEqual("call-19", vacationSetResponse[2].GetString());
            Assert.IsTrue(vacationSetResponse[1].GetProperty("updated").TryGetProperty("singleton", out _));
            Assert.AreEqual("notFound", vacationSetResponse[1].GetProperty("notUpdated")
                .GetProperty("missing").GetProperty("type").GetString());
            Assert.AreEqual("singleton", vacationSetResponse[1].GetProperty("notCreated")
                .GetProperty("new").GetProperty("type").GetString());
            Assert.AreEqual("singleton", vacationSetResponse[1].GetProperty("notDestroyed")
                .GetProperty("singleton").GetProperty("type").GetString());
            var queryCommand = jmap.Commands[18];
            Assert.AreEqual(MailOperationKind.FindSubmissions, queryCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                queryCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual("final", queryCommand.Arguments["criteria"]!["filter"]!["undoStatus"]!.GetValue<string>());
            Assert.AreEqual(1, queryCommand.Arguments["limit"]!.GetValue<int>());
            Assert.IsFalse(queryCommand.Arguments.ContainsKey("filter"));
            var queryResponse = json.RootElement.GetProperty("methodResponses")[19];
            Assert.AreEqual("EmailSubmission/query", queryResponse[0].GetString());
            Assert.AreEqual("call-20", queryResponse[2].GetString());
            Assert.AreEqual("S22222222222222222222222222222222",
                queryResponse[1].GetProperty("ids")[0].GetString());
            Assert.AreEqual(1, queryResponse[1].GetProperty("total").GetInt32());
            var queryChangesCommand = jmap.Commands[19];
            Assert.AreEqual(MailOperationKind.FindSubmissionChanges, queryChangesCommand.Operation);
            Assert.AreEqual("s50", queryChangesCommand.Arguments["sinceState"]!.GetValue<string>());
            Assert.AreEqual(2, queryChangesCommand.Arguments["maxChanges"]!.GetValue<int>());
            var queryChangesResponse = json.RootElement.GetProperty("methodResponses")[20];
            Assert.AreEqual("EmailSubmission/queryChanges", queryChangesResponse[0].GetString());
            Assert.AreEqual("call-21", queryChangesResponse[2].GetString());
            Assert.AreEqual("S22222222222222222222222222222222",
                queryChangesResponse[1].GetProperty("removed")[0].GetString());
            Assert.AreEqual("S33333333333333333333333333333333",
                queryChangesResponse[1].GetProperty("added")[0].GetProperty("id").GetString());
            var folderQueryCommand = jmap.Commands[20];
            Assert.AreEqual(MailOperationKind.FindFolders, folderQueryCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                folderQueryCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual("inbox", folderQueryCommand.Arguments["criteria"]!["filter"]!["role"]!.GetValue<string>());
            Assert.AreEqual(1, folderQueryCommand.Arguments["criteria"]!["sort"]!.AsArray().Count);
            Assert.IsTrue(folderQueryCommand.Arguments["criteria"]!["filterAsTree"]!.GetValue<bool>());
            Assert.IsFalse(folderQueryCommand.Arguments.ContainsKey("filter"));
            var folderQueryResponse = json.RootElement.GetProperty("methodResponses")[21];
            Assert.AreEqual("Mailbox/query", folderQueryResponse[0].GetString());
            Assert.AreEqual("call-22", folderQueryResponse[2].GetString());
            Assert.AreEqual("M22222222222222222222222222222222",
                folderQueryResponse[1].GetProperty("ids")[0].GetString());
            Assert.IsFalse(folderQueryResponse[1].GetProperty("canCalculateChanges").GetBoolean());
            Assert.AreEqual(1, folderQueryResponse[1].GetProperty("total").GetInt32());
            var folderChangesCommand = jmap.Commands[21];
            Assert.AreEqual(MailOperationKind.FindFolderChanges, folderChangesCommand.Operation);
            Assert.AreEqual("s52", folderChangesCommand.Arguments["sinceState"]!.GetValue<string>());
            Assert.AreEqual(2, folderChangesCommand.Arguments["maxChanges"]!.GetValue<int>());
            var folderChangesResponse = json.RootElement.GetProperty("methodResponses")[22];
            Assert.AreEqual("Mailbox/queryChanges", folderChangesResponse[0].GetString());
            Assert.AreEqual("call-23", folderChangesResponse[2].GetString());
            Assert.AreEqual("M22222222222222222222222222222222",
                folderChangesResponse[1].GetProperty("removed")[0].GetString());
            Assert.AreEqual("M33333333333333333333333333333333",
                folderChangesResponse[1].GetProperty("added")[0].GetProperty("id").GetString());
            var contactCopyCommand = jmap.Commands[22];
            Assert.AreEqual(MailOperationKind.CopyContacts, contactCopyCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                contactCopyCommand.Arguments["sourceAccountId"]!.GetValue<string>());
            Assert.AreEqual("22222222-2222-2222-2222-222222222222",
                contactCopyCommand.Arguments["targetAccountId"]!.GetValue<string>());
            Assert.AreEqual("copy", contactCopyCommand.Arguments["creationIds"]![0]!.GetValue<string>());
            Assert.IsFalse(contactCopyCommand.Arguments.ContainsKey("create"));
            var contactCopyResponse = json.RootElement.GetProperty("methodResponses")[23];
            Assert.AreEqual("ContactCard/copy", contactCopyResponse[0].GetString());
            Assert.AreEqual("call-24", contactCopyResponse[2].GetString());
            Assert.AreEqual("forbidden", contactCopyResponse[1].GetProperty("notCreated")
                .GetProperty("copy").GetProperty("type").GetString());
            var contactQueryCommand = jmap.Commands[23];
            Assert.AreEqual(MailOperationKind.FindContacts, contactQueryCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                contactQueryCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual("person@example.test", contactQueryCommand.Arguments["criteria"]!["filter"]!["terms"]![0]!["value"]!.GetValue<string>());
            Assert.IsFalse(contactQueryCommand.Arguments.ContainsKey("filter"));
            var contactQueryResponse = json.RootElement.GetProperty("methodResponses")[24];
            Assert.AreEqual("ContactCard/query", contactQueryResponse[0].GetString());
            Assert.AreEqual("call-25", contactQueryResponse[2].GetString());
            Assert.AreEqual("C22222222222222222222222222222222",
                contactQueryResponse[1].GetProperty("ids")[0].GetString());
            Assert.AreEqual(1, contactQueryResponse[1].GetProperty("total").GetInt32());
            var contactChangesCommand = jmap.Commands[24];
            Assert.AreEqual(MailOperationKind.FindContactChanges, contactChangesCommand.Operation);
            Assert.AreEqual("s55", contactChangesCommand.Arguments["sinceState"]!.GetValue<string>());
            Assert.AreEqual(2, contactChangesCommand.Arguments["maxChanges"]!.GetValue<int>());
            var contactChangesResponse = json.RootElement.GetProperty("methodResponses")[25];
            Assert.AreEqual("ContactCard/queryChanges", contactChangesResponse[0].GetString());
            Assert.AreEqual("call-26", contactChangesResponse[2].GetString());
            Assert.AreEqual("C22222222222222222222222222222222",
                contactChangesResponse[1].GetProperty("removed")[0].GetString());
            Assert.AreEqual("C33333333333333333333333333333333",
                contactChangesResponse[1].GetProperty("added")[0].GetProperty("id").GetString());
            var contactReadCommand = jmap.Commands[25];
            Assert.AreEqual(MailOperationKind.ReadContacts, contactReadCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                contactReadCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual(2, contactReadCommand.Arguments["cardIds"]!.AsArray().Count);
            Assert.IsFalse(contactReadCommand.Arguments.ContainsKey("properties"));
            var contactReadResponse = json.RootElement.GetProperty("methodResponses")[26];
            Assert.AreEqual("ContactCard/get", contactReadResponse[0].GetString());
            Assert.AreEqual("call-27", contactReadResponse[2].GetString());
            Assert.AreEqual("contact-uid", contactReadResponse[1].GetProperty("list")[0]
                .GetProperty("uid").GetString());
            Assert.AreEqual("D44444444444444444444444444444444", contactReadResponse[1]
                .GetProperty("list")[0].GetProperty("addressBookIds").EnumerateObject().Single().Name);
            Assert.IsFalse(contactReadResponse[1].GetProperty("list")[0].TryGetProperty("version", out _));
            Assert.AreEqual("C33333333333333333333333333333333", contactReadResponse[1]
                .GetProperty("notFound")[0].GetString());
            var importCommand = jmap.Commands[26];
            Assert.AreEqual(MailOperationKind.ImportMessages, importCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                importCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual("draft", importCommand.Arguments["items"]![0]!["creationId"]!.GetValue<string>());
            Assert.AreEqual("33333333-3333-3333-3333-333333333333",
                importCommand.Arguments["items"]![0]!["mailboxId"]!.GetValue<string>());
            Assert.IsFalse(importCommand.Arguments.ContainsKey("emails"));
            var importResponse = json.RootElement.GetProperty("methodResponses")[27];
            Assert.AreEqual("Email/import", importResponse[0].GetString());
            Assert.AreEqual("call-28", importResponse[2].GetString());
            Assert.AreEqual("E55555555555555555555555555555555", importResponse[1]
                .GetProperty("created").GetProperty("draft").GetProperty("id").GetString());
            Assert.AreEqual("B55555555555555555555555555555555", importResponse[1]
                .GetProperty("created").GetProperty("draft").GetProperty("blobId").GetString());
            var emailCopyCommand = jmap.Commands[27];
            Assert.AreEqual(MailOperationKind.CopyMessages, emailCopyCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                emailCopyCommand.Arguments["sourceAccountId"]!.GetValue<string>());
            Assert.AreEqual("22222222-2222-2222-2222-222222222222",
                emailCopyCommand.Arguments["targetAccountId"]!.GetValue<string>());
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                emailCopyCommand.Arguments["items"]![0]!["sourceEmailId"]!.GetValue<string>());
            Assert.IsFalse(emailCopyCommand.Arguments.ContainsKey("create"));
            var emailCopyResponse = json.RootElement.GetProperty("methodResponses")[28];
            Assert.AreEqual("Email/copy", emailCopyResponse[0].GetString());
            Assert.AreEqual("call-29", emailCopyResponse[2].GetString());
            Assert.AreEqual("E77777777777777777777777777777777", emailCopyResponse[1]
                .GetProperty("created").GetProperty("copied").GetProperty("id").GetString());
            var destroyResponse = json.RootElement.GetProperty("methodResponses")[29];
            Assert.AreEqual("Email/set", destroyResponse[0].GetString());
            Assert.AreEqual("E11111111111111111111111111111111", destroyResponse[1]
                .GetProperty("destroyed")[0].GetString());
            var messageQueryCommand = jmap.Commands[28];
            Assert.AreEqual(MailOperationKind.FindMessages, messageQueryCommand.Operation);
            Assert.AreEqual("11111111-1111-1111-1111-111111111111",
                messageQueryCommand.Arguments["accountId"]!.GetValue<string>());
            Assert.AreEqual("needle", messageQueryCommand.Arguments["criteria"]!["filter"]!["terms"]![0]!["text"]!
                .GetValue<string>());
            Assert.AreEqual("$seen", messageQueryCommand.Arguments["criteria"]!["filter"]!["terms"]![1]!["text"]!
                .GetValue<string>());
            Assert.AreEqual(1, messageQueryCommand.Arguments["limit"]!.GetValue<int>());
            Assert.IsFalse(messageQueryCommand.Arguments.ContainsKey("filter"));
            var messageQueryResponse = json.RootElement.GetProperty("methodResponses")[30];
            Assert.AreEqual("Email/query", messageQueryResponse[0].GetString());
            Assert.AreEqual("call-30", messageQueryResponse[2].GetString());
            Assert.AreEqual("E22222222222222222222222222222222",
                messageQueryResponse[1].GetProperty("ids")[0].GetString());
            Assert.AreEqual(1, messageQueryResponse[1].GetProperty("total").GetInt32());
            var messageChangesCommand = jmap.Commands[29];
            Assert.AreEqual(MailOperationKind.FindMessageChanges, messageChangesCommand.Operation);
            Assert.AreEqual("s62", messageChangesCommand.Arguments["sinceState"]!.GetValue<string>());
            Assert.AreEqual(2, messageChangesCommand.Arguments["maxChanges"]!.GetValue<int>());
            var messageChangesResponse = json.RootElement.GetProperty("methodResponses")[31];
            Assert.AreEqual("Email/queryChanges", messageChangesResponse[0].GetString());
            Assert.AreEqual("call-31", messageChangesResponse[2].GetString());
            Assert.AreEqual("E22222222222222222222222222222222",
                messageChangesResponse[1].GetProperty("removed")[0].GetString());
            Assert.AreEqual("E33333333333333333333333333333333",
                messageChangesResponse[1].GetProperty("added")[0].GetProperty("id").GetString());
            var snippetCommand = jmap.Commands[30];
            Assert.AreEqual(MailOperationKind.ReadSearchSnippets, snippetCommand.Operation);
            Assert.IsFalse(snippetCommand.Arguments.ContainsKey("filter"));
            Assert.AreEqual("needle", snippetCommand.Arguments["terms"]![0]!.GetValue<string>());
            Assert.AreEqual(1, snippetCommand.Arguments["messageIds"]!.AsArray().Count);
            var snippetResponse = json.RootElement.GetProperty("methodResponses")[32];
            Assert.AreEqual("SearchSnippet/get", snippetResponse[0].GetString());
            Assert.AreEqual("call-32", snippetResponse[2].GetString());
            Assert.AreEqual("<mark>needle</mark> subject",
                snippetResponse[1].GetProperty("list")[0].GetProperty("subject").GetString());
            Assert.AreEqual("missing", snippetResponse[1].GetProperty("notFound")[0].GetString());
            var identitySetCommand = jmap.Commands[31];
            Assert.AreEqual(MailOperationKind.MutateSenderIdentities, identitySetCommand.Operation);
            Assert.AreEqual("person@example.test", identitySetCommand.Arguments["creates"]![0]!["values"]!["email"]!
                .GetValue<string>());
            Assert.IsFalse(identitySetCommand.Arguments.ContainsKey("create"));
            var identitySetResponse = json.RootElement.GetProperty("methodResponses")[33];
            Assert.AreEqual("Identity/set", identitySetResponse[0].GetString());
            Assert.AreEqual("I55555555555555555555555555555555",
                identitySetResponse[1].GetProperty("created").GetProperty("route-identity")
                    .GetProperty("id").GetString());
            var bookSetCommand = jmap.Commands[32];
            Assert.AreEqual(MailOperationKind.MutateAddressBooks, bookSetCommand.Operation);
            Assert.AreEqual("Route Book", bookSetCommand.Arguments["creates"]![0]!["values"]!["name"]!
                .GetValue<string>());
            Assert.AreEqual("route-book", bookSetCommand.Arguments["onSuccessSetIsDefault"]!["createdKey"]!
                .GetValue<string>());
            Assert.IsFalse(bookSetCommand.Arguments.ContainsKey("create"));
            var bookSetResponse = json.RootElement.GetProperty("methodResponses")[34];
            Assert.AreEqual("AddressBook/set", bookSetResponse[0].GetString());
            Assert.AreEqual("D88888888888888888888888888888888",
                bookSetResponse[1].GetProperty("created").GetProperty("route-book").GetProperty("id").GetString());
            Assert.IsTrue(bookSetResponse[1].GetProperty("created").GetProperty("route-book")
                .GetProperty("isDefault").GetBoolean());
            Assert.IsFalse(bookSetResponse[1].GetProperty("updated").GetProperty("D22222222222222222222222222222222")
                .GetProperty("isDefault").GetBoolean());
            var pushSetCommand = jmap.Commands[33];
            Assert.AreEqual(MailOperationKind.MutateNotificationSubscriptions, pushSetCommand.Operation);
            Assert.AreEqual("route-device", pushSetCommand.Arguments["creates"]![0]!["values"]!["deviceClientId"]!
                .GetValue<string>());
            Assert.IsFalse(pushSetCommand.Arguments.ContainsKey("create"));
            var pushSetResponse = json.RootElement.GetProperty("methodResponses")[35];
            Assert.AreEqual("PushSubscription/set", pushSetResponse[0].GetString());
            Assert.AreEqual("P99999999999999999999999999999999",
                pushSetResponse[1].GetProperty("created").GetProperty("route-push").GetProperty("id").GetString());
            var contactSetCommand = jmap.Commands[34];
            Assert.AreEqual(MailOperationKind.MutateContacts, contactSetCommand.Operation);
            Assert.IsFalse(contactSetCommand.Arguments.ContainsKey("create"));
            Assert.AreEqual("88888888-8888-8888-8888-888888888888",
                contactSetCommand.Arguments["creates"]![0]!["addressBookId"]!.GetValue<string>());
            var contactSetResponse = json.RootElement.GetProperty("methodResponses")[36];
            Assert.AreEqual("ContactCard/set", contactSetResponse[0].GetString());
            Assert.AreEqual("C77777777777777777777777777777777",
                contactSetResponse[1].GetProperty("created").GetProperty("route-card")
                    .GetProperty("id").GetString());
            var folderSetCommand = jmap.Commands[35];
            Assert.AreEqual(MailOperationKind.MutateFolders, folderSetCommand.Operation);
            Assert.IsFalse(folderSetCommand.Arguments.ContainsKey("create"));
            Assert.AreEqual("route-folder", folderSetCommand.Arguments["creates"]![0]!["creationId"]!
                .GetValue<string>());
            var folderSetResponse = json.RootElement.GetProperty("methodResponses")[37];
            Assert.AreEqual("Mailbox/set", folderSetResponse[0].GetString());
            Assert.AreEqual("Maaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                folderSetResponse[1].GetProperty("created").GetProperty("route-folder")
                    .GetProperty("id").GetString());

            await using var countCommand = gatewayDataSource.CreateCommand(
                "SELECT count(*), count(*) FILTER (WHERE metadata ->> 'layer' = 'presentation') "
                + "FROM gateway_traffic_records WHERE protocol = 'jmap'");
            await using var countReader = await countCommand.ExecuteReaderAsync(timeout.Token);
            Assert.IsTrue(await countReader.ReadAsync(timeout.Token));
            // SSE records its headers and streamed body separately, in addition
            // to the request and two application-boundary records.
            Assert.AreEqual(85L, countReader.GetInt64(0));
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
            if (request.Command.Operation == MailOperationKind.CopyBinaryObjects)
            {
                var copied = new MailBlobCopyResult(MailBlobCopyStatus.Ok,
                    [new MailBlobCopyItemResult("U44444444444444444444444444444444",
                        MailBlobCopyItemStatus.Copied, "U55555555555555555555555555555555"),
                     new MailBlobCopyItemResult("U66666666666666666666666666666666",
                        MailBlobCopyItemStatus.NotFound, null)]);
                var node = JsonSerializer.SerializeToNode(copied, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.CopyBinaryObjects, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.MutateVacationSettings)
            {
                var mutation = new MailVacationSetResult(MailVacationSetStatus.Ok, "s48", "s49", [new(true, [])]);
                var node = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.MutateVacationSettings, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindSubmissions)
            {
                var query = new MailSubmissionQueryResult(MailSubmissionQueryStatus.Ok, "s50", 0,
                    [Guid.Parse("22222222-2222-2222-2222-222222222222")], 1);
                var node = JsonSerializer.SerializeToNode(query, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindSubmissions, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindSubmissionChanges)
            {
                var changes = new MailSubmissionQueryChangesResult(MailSubmissionQueryStatus.Ok, "s51",
                    ["S22222222222222222222222222222222"],
                    [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0)], 1);
                var node = JsonSerializer.SerializeToNode(changes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindSubmissionChanges, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindFolders)
            {
                var query = new MailFolderQueryResult(MailFolderQueryStatus.Ok, "s52", 0,
                    [Guid.Parse("22222222-2222-2222-2222-222222222222")], 1);
                var node = JsonSerializer.SerializeToNode(query, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindFolders, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindFolderChanges)
            {
                var changes = new MailFolderQueryChangesResult(MailFolderQueryStatus.Ok, "s53",
                    ["M22222222222222222222222222222222"],
                    [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0)], 1);
                var node = JsonSerializer.SerializeToNode(changes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindFolderChanges, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindMessages)
            {
                var query = new MailMessageQueryResult(MailMessageQueryStatus.Ok, "s62", 0,
                    [Guid.Parse("22222222-2222-2222-2222-222222222222")], 1);
                var node = JsonSerializer.SerializeToNode(query, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindMessages, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindMessageChanges)
            {
                var changes = new MailMessageQueryChangesResult(MailMessageQueryStatus.Ok, "s63",
                    ["E22222222222222222222222222222222"],
                    [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0)], 1);
                var node = JsonSerializer.SerializeToNode(changes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindMessageChanges, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadSearchSnippets)
            {
                var snippets = new MailSearchSnippetResult(MailSearchSnippetStatus.Ok,
                    [new(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "needle subject", "before needle after")]);
                var node = JsonSerializer.SerializeToNode(snippets, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadSearchSnippets, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.MutateSenderIdentities)
            {
                var mutation = new MailIdentityMutationResult(MailIdentityMutationStatus.Ok, "s64", "s65",
                    [new("route-identity", Guid.Parse("55555555-5555-5555-5555-555555555555"),
                        MailIdentityMutationError.None)], [], []);
                var node = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.MutateSenderIdentities, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.MutateAddressBooks)
            {
                var created = new MailAddressBookSnapshot(Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    "Route Book", null, 0, true, true, true);
                var formerDefault = new MailAddressBookSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "Default", null, 0, false, true, true);
                var mutation = new MailAddressBookMutationResult(MailAddressBookMutationStatus.Ok, "s66", "s67",
                    [new("route-book", created, MailAddressBookMutationError.None, null)], [], [],
                    [formerDefault, created]);
                var node = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var known = request.Command.KnownEntities!.ToDictionary(item => item.Key, item => item.Value,
                    StringComparer.Ordinal);
                known["route-book"] = "D88888888888888888888888888888888";
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.MutateAddressBooks, ApplicationValueCodec.Encode(node)),
                        known, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.MutateNotificationSubscriptions)
            {
                var mutation = new MailPushSubscriptionMutationResult(
                    [new("route-push", Guid.Parse("99999999-9999-9999-9999-999999999999"),
                        MailPushSubscriptionMutationError.None,
                        new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, null)], [], []);
                var node = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.MutateNotificationSubscriptions,
                        ApplicationValueCodec.Encode(node)), request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.MutateContacts)
            {
                var card = new JsonObject { ["@type"] = "Card", ["version"] = "1.0", ["uid"] = "route-uid" };
                var mutation = new MailContactMutationResult(MailContactMutationStatus.Ok, "s68", "s69",
                    [new("route-card", Guid.Parse("77777777-7777-7777-7777-777777777777"),
                        ApplicationValueCodec.Encode(card), MailContactMutationError.None, null)], [], []);
                var node = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.MutateContacts, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.MutateFolders)
            {
                var folder = new MailFolderCreatedSnapshot(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Route Folder", null, null, 0, true);
                var mutation = new MailFolderMutationResult(MailFolderMutationStatus.Ok, "s70", "s71",
                    [new("route-folder", folder, null)], [], []);
                var node = JsonSerializer.SerializeToNode(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var known = request.Command.KnownEntities!.ToDictionary(item => item.Key, item => item.Value,
                    StringComparer.Ordinal);
                known["route-folder"] = "Maaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.MutateFolders, ApplicationValueCodec.Encode(node)),
                        known, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.CopyContacts)
            {
                var copy = new MailContactCopyResult(MailContactCopyStatus.Ok, "s54");
                var node = JsonSerializer.SerializeToNode(copy, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.CopyContacts, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindContacts)
            {
                var query = new MailContactQueryResult(MailContactQueryStatus.Ok, "s55", 0,
                    [Guid.Parse("22222222-2222-2222-2222-222222222222")], 1);
                var node = JsonSerializer.SerializeToNode(query, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindContacts, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.FindContactChanges)
            {
                var changes = new MailContactQueryChangesResult(MailContactQueryStatus.Ok, "s56",
                    ["C22222222222222222222222222222222"],
                    [new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0)], 1);
                var node = JsonSerializer.SerializeToNode(changes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.FindContactChanges, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ReadContacts)
            {
                var read = new MailContactReadResult(MailContactReadStatus.Ok, "s57",
                    [new MailContactCardSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Guid.Parse("44444444-4444-4444-4444-444444444444"), "contact-uid",
                        """{"@type":"Card","version":"1.0","uid":"contact-uid","name":{"full":"Contact Person"}}""")]);
                var node = JsonSerializer.SerializeToNode(read, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ReadContacts, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.ImportMessages)
            {
                var imported = new MailImportResult(MailImportStatus.Ok, "s57", "s58",
                    [new("draft", MailImportItemError.None,
                        Guid.Parse("55555555-5555-5555-5555-555555555555"), "thread:import", 123)]);
                var node = JsonSerializer.SerializeToNode(imported, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.ImportMessages, ApplicationValueCodec.Encode(node)),
                        request.Command.KnownEntities!, Profile)));
            }
            if (request.Command.Operation == MailOperationKind.CopyMessages)
            {
                var copied = new MailCopyResult(MailCopyStatus.Ok, "s59", "s60",
                    [new("copied", MailCopyItemError.None,
                        Guid.Parse("77777777-7777-7777-7777-777777777777"), "thread:copy", 234)],
                    new(MailCopyDestroyStatus.Completed, "s58", "s61",
                        [Guid.Parse("11111111-1111-1111-1111-111111111111")], []));
                var node = JsonSerializer.SerializeToNode(copied, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return Task.FromResult(new JmapApplicationResult(JmapApplicationOutcomes.Ok,
                    OperationResult: new(new(MailOperationKind.CopyMessages, ApplicationValueCodec.Encode(node)),
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
