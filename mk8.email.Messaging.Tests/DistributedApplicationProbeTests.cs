using System.Text.Json;
using mk8.email.Contracts.Messaging;
using mk8.email.Hosting;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class DistributedApplicationProbeTests
{
    [TestMethod]
    [DataRow(DistributedContractVersions.Current, true)]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("mk8.distributed.v3", false)]
    [DataRow("mk8.distributed.v6", false)]
    [DataRow("different-contract", false)]
    [DataRow("MK8.distributed.v7", false)]
    public async Task ActivationProbeRequiresMatchingWorkerContracts(string? version, bool compatible)
    {
        var client = new ProbeClient(version);
        if (compatible)
            await DistributedApplicationProbe.ProbeAsync(client, TimeSpan.FromSeconds(5));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => DistributedApplicationProbe.ProbeAsync(client, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(ApplicationOperations.SystemPing, client.Request?.Operation);
        Assert.AreEqual("health", client.Request?.Protocol);
    }

    private sealed class ProbeClient(string? version) : IApplicationRequestClient
    {
        public ApplicationRequest? Request { get; private set; }
        public Task<ApplicationResponse> SendAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ApplicationResponse(request.Id, "application/json",
                JsonSerializer.SerializeToUtf8Bytes(new SystemPingResult(DateTimeOffset.UtcNow, version), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new Dictionary<string, string>()));
        }

        public Task EnqueueAsync(ApplicationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApplicationResponse> WaitForResponseAsync(Guid requestId, DateTimeOffset deadline, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApplicationExchangeSnapshot?> GetAsync(Guid requestId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
