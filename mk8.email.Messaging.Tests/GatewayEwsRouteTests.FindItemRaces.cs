using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using CaptureFixture = mk8.email.Messaging.Tests.GatewayHttpCaptureBoundaryTests.CaptureFixture;

namespace mk8.email.Messaging.Tests;

internal sealed partial class GatewayEwsRouteTests
{
    [TestMethod]
    [DataRow("message")]
    [DataRow("scope")]
    [DataRow("folder")]
    public async Task CommittedWriteBetweenRealWorkerOperationsProducesJournaledBusyNotMixedItems(string change)
    {
        CaptureFixture? fixture = null;
        Guid item = Guid.Empty;
        var injections = 0;
        var trigger = change is "folder" ? MailOperationKind.ReadMessages : MailOperationKind.FindMessages;
        fixture = await CaptureFixture.CreateAsync(mailFolders: true, decorateDispatcher: inner => new FindRaceDispatcher(inner, trigger, async token =>
        {
            if (Interlocked.CompareExchange(ref injections, 1, 0) != 0) return;
            var database = Context(fixture!);
            await using var databaseLifetime = database.ConfigureAwait(false);
            if (change is "folder")
                (await database.Folders.SingleAsync(folder => folder.Id == GatewayEwsFixtureDomain.ChildId, token).ConfigureAwait(false)).SortOrder++;
            else
            {
                var email = await database.Emails.SingleAsync(email => email.Id == item, token).ConfigureAwait(false);
                if (change is "scope") email.FolderId = GatewayEwsFixtureDomain.ForeignFolderId;
                else email.IsRead = true;
            }
            await database.SaveChangesAsync(token).ConfigureAwait(false);
        })).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        item = (await SeedFindItemsAsync(fixture, 1, GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false)).Single();
        Authenticate(fixture);
        using var content = XmlContent(FindRequest($"<t:FolderId Id='{FolderId(GatewayEwsFixtureDomain.ChildId)}'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(1, injections);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertFault(body, "ErrorServerBusy");
        Assert.IsFalse(body.Contains("ItemId", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("NoError", StringComparison.Ordinal));
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, 503, body, rejection: false).ConfigureAwait(false);
        var persisted = Context(fixture);
        await using var persistedLifetime = persisted.ConfigureAwait(false);
        if (change is "folder") Assert.AreEqual(1L, (await persisted.Folders.SingleAsync(folder => folder.Id == GatewayEwsFixtureDomain.ChildId).ConfigureAwait(false)).SortOrder);
        else if (change is "scope") Assert.AreEqual(GatewayEwsFixtureDomain.ForeignFolderId, (await persisted.Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false)).FolderId);
        else Assert.IsTrue((await persisted.Emails.SingleAsync(email => email.Id == item).ConfigureAwait(false)).IsRead);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("wrong")]
    [DataRow("plaintext")]
    public async Task FindItemRequiresTlsAndWorkerAuthenticatedPasswordAuthority(string mode)
    {
        var fixture = await CaptureFixture.CreateAsync(mailFolders: true).ConfigureAwait(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        Authenticate(fixture);
        if (mode is "missing") fixture.Client.DefaultRequestHeaders.Authorization = null;
        if (mode is "wrong") fixture.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("owner@example.test:wrong")));
        if (mode is "plaintext")
        {
            fixture.Client.DefaultRequestHeaders.Remove("X-Forwarded-Proto");
            fixture.Client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        }
        using var content = XmlContent(FindRequest("<t:DistinguishedFolderId Id='inbox'/>"));
        using var response = await fixture.Client.PostAsync(new Uri(CanonicalPath, UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = mode is "plaintext" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        Assert.AreEqual(status, response.StatusCode);
        AssertFault(body, "ErrorAccessDenied");
        if (mode is "wrong") await fixture.AssertWorkerOperationsAsync(ApplicationOperations.JmapProfileGet).ConfigureAwait(false);
        else await fixture.AssertNoWorkerRequestsAsync().ConfigureAwait(false);
        await fixture.AssertRecordedResponseAsync("ews", CanonicalPath, (int)status, body, rejection: false).ConfigureAwait(false);
    }

    private sealed class FindRaceDispatcher(IApplicationRequestDispatcher inner, MailOperationKind trigger,
        Func<CancellationToken, Task> mutate) : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            // Execute the actual Worker/receipt/domain path first; inject only after its transaction committed.
            var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.Equals(request.Operation, ApplicationOperations.MailOperationExecute, StringComparison.Ordinal)
                && JsonSerializer.Deserialize<MailOperationApplicationRequest>(request.Payload, JsonSerializerOptions.Web)!.Command.Operation == trigger)
                await mutate(cancellationToken).ConfigureAwait(false);
            return response;
        }
    }
}
