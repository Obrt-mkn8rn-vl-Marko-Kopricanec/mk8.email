using System.Net;
using System.Net.Sockets;
using mk8.email.MailWire;

namespace mk8.email.Messaging.Tests;

internal sealed class NativeSmtpClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private NativeSmtpClient(TcpClient client)
    {
        _client = client;
        _reader = new StreamReader(client.GetStream(), MailWireEncoding.Instance, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        try
        {
            _writer = new StreamWriter(client.GetStream(), MailWireEncoding.Instance, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public static async Task<NativeSmtpClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            return new NativeSmtpClient(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<string?> ReadAsync(CancellationToken cancellationToken) => _reader.ReadLineAsync(cancellationToken).AsTask();
    public Task WriteAsync(string command, CancellationToken cancellationToken) =>
        _writer.WriteLineAsync(command.AsMemory(), cancellationToken);

    public async Task GreetAsync(CancellationToken cancellationToken)
    {
        Assert.StartsWith("220 ", await ReadAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal);
        await WriteAsync("EHLO inbound.example.test", cancellationToken).ConfigureAwait(false);
        string? line;
        do
        {
            line = await ReadAsync(cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(line);
            Assert.StartsWith("250", line, StringComparison.Ordinal);
        } while (line.StartsWith("250-", StringComparison.Ordinal));
    }

    public async Task BeginMessageAsync(string recipient, CancellationToken cancellationToken)
    {
        await WriteAsync("MAIL FROM:<sender@remote.test> BODY=8BITMIME", cancellationToken).ConfigureAwait(false);
        Assert.StartsWith("250 ", await ReadAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal);
        await WriteAsync($"RCPT TO:<{recipient}>", cancellationToken).ConfigureAwait(false);
    }

    public Task WriteDataAsync(string raw, CancellationToken cancellationToken) =>
        _writer.WriteAsync((raw.Replace("\r\n.", "\r\n..", StringComparison.Ordinal) + ".\r\n").AsMemory(), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await _writer.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { _reader.Dispose(); }
            finally { _client.Dispose(); }
        }
    }
}
