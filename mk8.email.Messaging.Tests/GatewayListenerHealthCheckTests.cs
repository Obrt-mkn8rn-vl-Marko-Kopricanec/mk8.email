using System.Net;
using System.Net.Sockets;
using mk8.email.Configuration;
using mk8.email.Hosting;

namespace mk8.email.Messaging.Tests;

[TestClass]
public sealed class GatewayListenerHealthCheckTests
{
    [TestMethod]
    public async Task EveryEnabledPop3AndSieveListenerMustBeReachable()
    {
        using var pop3 = new TcpListener(IPAddress.Loopback, 0);
        using var sieve = new TcpListener(IPAddress.Loopback, 0);
        pop3.Start();
        sieve.Start();
        var pop3Port = ((IPEndPoint)pop3.LocalEndpoint).Port;
        var sievePort = ((IPEndPoint)sieve.LocalEndpoint).Port;
        var environment = new EnvironmentConfig
        {
            Smtp = new SmtpConfig { EnableSmtp = false },
            Imap = new ImapConfig { EnableImap = false },
            Pop3 = new Pop3Config { EnablePop3 = true, Port = pop3Port },
            Sieve = new SieveConfig { EnableManageSieve = true, Port = sievePort },
        };

        Assert.IsTrue(await GatewayListenerHealthCheck.IsHealthyAsync(
            environment,
            CancellationToken.None));
        sieve.Stop();
        Assert.IsFalse(await GatewayListenerHealthCheck.IsHealthyAsync(
            environment,
            CancellationToken.None));
    }
}
