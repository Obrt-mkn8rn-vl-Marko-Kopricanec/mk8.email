using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Configuration;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Messaging.Tests;

internal sealed class SmtpListenerFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _stops = [];
    private int _disposedScopes;
    public SmtpServerService Server { get; }
    public Task Completing { get; }
    public SmtpListenerSignals Signals { get; } = new();
    public SmtpListenerHold Hold { get; }
    public SmtpListenerRecipientHold RecipientHold { get; } = new();
    public int DisposedScopes => Volatile.Read(ref _disposedScopes);

    public SmtpListenerFixture(SmtpConfig smtp, bool hold = true, bool hideInspectionTask = false)
    {
        Hold = new SmtpListenerHold { Enabled = hold };
        _services = new ServiceCollection().AddScoped<ISmtpApplicationService>(_ => new SmtpListenerScope(this))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        var environment = new EnvironmentConfig { Smtp = smtp };
        Server = hideInspectionTask
            ? new SmtpListenerMaskedService(scopes, environment, Signals, Hold)
            : new SmtpServerService(scopes, environment, Signals, Hold);
        // BackgroundService.StartAsync publishes ExecuteTask synchronously, even
        // though .NET 10 runs ExecuteAsync in the background. No private handler
        // reflection or substitute listener is used in these controls.
        _stops.Add(Server.StartAsync(CancellationToken.None));
        Completing = (Server is SmtpListenerMaskedService masked ? masked.ActualCompleting : Server.ExecuteTask)
            ?? throw new InvalidOperationException("The actual SMTP execution task was not published.");
    }

    public async Task<TcpClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        _clients.Add(client); // Own acquisition before connect can fail.
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        return client;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var stopping = Server.StopAsync(cancellationToken);
        _stops.Add(stopping);
        return stopping;
    }

    public void RecordScopeDisposal() => Interlocked.Increment(ref _disposedScopes);

    public async Task JoinAsync(CancellationTokenSource deadline, Exception? originalFailure, bool expectedBindFailure = false)
    {
        Hold.Release();
        RecipientHold.Release();
        _ = StopAsync(CancellationToken.None); // Stored in _stops before the bounded join.
        var owned = Task.WhenAll(_stops.Append(Completing));
        try
        {
            // This inventory owns the actual service/stop tasks, not a cancellation request alone.
#pragma warning disable VSTHRD003
            await GatewayEwsRouteTests.ObserveWriterCleanupAsync(owned, writer: null, deadline, originalFailure).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        catch (AggregateException errors) when (expectedBindFailure && originalFailure is null
            && errors.Flatten().InnerExceptions.All(error => error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }))
        {
            // Only the controlled occupied-port failure already asserted by the test is expected.
        }
        catch (AggregateException errors) when (originalFailure is null && RecipientHold.FailureObserved
            && errors.Flatten().InnerExceptions.All(error => ReferenceEquals(error, RecipientHold.Failure)))
        {
            // Only the exact callback fault asserted through repeated actual stops is expected.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Completing.IsCompleted || _stops.Exists(task => !task.IsCompleted))
            throw new InvalidOperationException("Actual SMTP work is still active; dependency retirement is refused.");
        foreach (var client in _clients) client.Dispose();
        Server.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
    }

    public static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
