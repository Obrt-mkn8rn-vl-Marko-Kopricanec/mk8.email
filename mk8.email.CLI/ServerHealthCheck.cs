using System.Net;
using System.Net.Sockets;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.CLI;

internal static class ServerHealthCheck
{
    public static async Task<bool> IsHealthyAsync(EnvironmentConfig environment, CancellationToken cancellationToken)
    {
        foreach (var port in GetEnabledPorts(environment))
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<int> GetEnabledPorts(EnvironmentConfig environment)
    {
        if (environment.Smtp.EnableSmtp)
            yield return environment.Smtp.Port;
        if (environment.Smtp.EnableSubmission)
            yield return environment.Smtp.SubmissionPort;
        if (environment.Smtp.EnableImplicitTls)
            yield return environment.Smtp.ImplicitTlsPort;
        if (environment.Imap.EnableImap)
            yield return environment.Imap.Port;
        if (environment.Imap.EnableImplicitTls)
            yield return environment.Imap.ImplicitTlsPort;
        if (environment.Jmap.EnableJmap)
            yield return environment.Jmap.Port;
    }
}
