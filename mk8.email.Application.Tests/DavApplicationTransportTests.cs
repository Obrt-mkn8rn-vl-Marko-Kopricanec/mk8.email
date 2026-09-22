using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Services;
using mk8.email.Contracts.Messaging;
using mk8.email.Dav;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class DavApplicationTransportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task DavOperationsDispatchWithoutHttpTypesAndVerifyCollectionAccess()
    {
        await using var fixture = await DavFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var dispatcher = new ApplicationRequestDispatcher(scope.ServiceProvider);

        var authentication = await DispatchAsync<DavLookupResult<DavUser>>(
            dispatcher,
            ApplicationOperations.DavAuthenticate,
            new DavAuthenticationRequest(new ProtocolAuthentication(
                ProtocolAuthenticationKinds.Password,
                fixture.PrimaryAddress,
                "correct horse battery staple")));
        var user = authentication.Value;
        Assert.IsNotNull(user);
        Assert.AreEqual(fixture.UserId, user.Id);

        var ensured = await DispatchAsync<DavAcknowledgement>(
            dispatcher,
            ApplicationOperations.DavEnsureCollections,
            new DavEnsureCollectionsRequest(user));
        Assert.IsTrue(ensured.Succeeded);
        var collection = (await DispatchAsync<DavLookupResult<DavCollection>>(
            dispatcher,
            ApplicationOperations.DavCollectionGet,
            new DavCollectionLookupRequest(
                user, DavCollectionKind.AddressBook, user.Id, "default"))).Value;
        Assert.IsNotNull(collection);

        var content = Encoding.UTF8.GetBytes(
            "BEGIN:VCARD\r\nVERSION:4.0\r\nUID;VALUE=text:transport-card\r\nFN:Transport Card\r\nEND:VCARD\r\n");
        var write = await DispatchAsync<DavResourceWriteResult>(
            dispatcher,
            ApplicationOperations.DavResourcePut,
            new DavResourcePutRequest(
                user, collection.Id, "transport.vcf", "transport-card", "text/vcard",
                content, null, true));
        Assert.AreEqual(DavResourceWriteStatus.Created, write.Status);

        var reference = new DavCollectionReference(user, collection);
        var resource = (await DispatchAsync<DavLookupResult<DavResource>>(
            dispatcher,
            ApplicationOperations.DavResourceGet,
            new DavResourceLookupRequest(reference, "transport.vcf"))).Value;
        Assert.IsNotNull(resource);
        CollectionAssert.AreEqual(content, resource.Content);

        var outsider = new DavUser(fixture.OutsiderUserId, "dav.outsider@other.example");
        var denied = await DispatchAsync<DavLookupResult<DavResource>>(
            dispatcher,
            ApplicationOperations.DavResourceGet,
            new DavResourceLookupRequest(
                new DavCollectionReference(outsider, collection),
                "transport.vcf"));
        Assert.IsNull(denied.Value);
    }

    private static async Task<T> DispatchAsync<T>(
        ApplicationRequestDispatcher dispatcher,
        string operation,
        object value)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new ApplicationRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 0, "dav", operation,
            "application/json", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            new Dictionary<string, string>(), now, now.AddMinutes(1));
        var response = await dispatcher.DispatchAsync(request);
        Assert.IsFalse(response.IsError, response.ErrorDetail);
        return JsonSerializer.Deserialize<T>(response.Payload, JsonOptions)
            ?? throw new AssertFailedException("The DAV application response was empty.");
    }
}
