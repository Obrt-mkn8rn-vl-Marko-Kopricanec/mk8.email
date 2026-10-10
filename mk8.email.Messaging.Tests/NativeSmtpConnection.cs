using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Configuration;
using mk8.email.Messaging;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Messaging.Tests;

// Own one actual production connection-handler invocation. The production
// wildcard accept loop/listener startup is deliberately not substituted as proof.
internal sealed class NativeSmtpConnection : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly SmtpServerService _server;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<Task> _body = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MethodInfo _handler;
    private readonly object _mode;
    private readonly object _options;
    public Task Completing { get; }
    public int Port { get; }

    public NativeSmtpConnection(IServiceScopeFactory scopes, EnvironmentConfig environment, IGatewayTrafficJournal journal)
    {
        Completing = _body.Task.Unwrap();
        _handler = typeof(SmtpServerService).GetMethod("HandleConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The production SMTP connection handler is missing.");
        var parameters = _handler.GetParameters();
        _mode = Enum.Parse(parameters[1].ParameterType, "Smtp");
        _options = parameters[2].ParameterType.GetMethod("FromEnvironment", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, [environment]) ?? throw new InvalidOperationException("The production SMTP listener options are missing.");
        _server = new SmtpServerService(scopes, environment, NullLogger<SmtpServerService>.Instance, journal);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _body.SetResult(RunConnectionAsync());
        }
        catch
        {
            _listener.Dispose();
            _server.Dispose();
            _stopping.Dispose();
            throw;
        }
    }

    private async Task RunConnectionAsync()
    {
        using var accepted = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
        var handling = _handler.Invoke(_server, [accepted, _mode, _options, _stopping.Token]) as Task
            ?? throw new InvalidOperationException("The production SMTP handler did not return its task.");
        await handling.ConfigureAwait(false);
    }

    public Task RequestStopAsync() => _stopping.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            // This constructor-published proxy owns the actual handler started by this fixture.
#pragma warning disable VSTHRD003
            await GatewayEwsRouteTests.ObserveWriterCleanupAsync(Completing, writer: null, _stopping, originalFailure: null).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        finally
        {
            // A bounded observation timeout cannot authorize dependency disposal
            // while the actual handler remains active. Surface that failure instead.
            if (Completing.IsCompleted)
            {
                _listener.Stop();
                _listener.Dispose();
                _server.Dispose();
                _stopping.Dispose();
            }
        }
    }
}
