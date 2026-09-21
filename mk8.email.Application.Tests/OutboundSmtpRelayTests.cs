using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnsClient;
using Microsoft.Extensions.Logging.Abstractions;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Application.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OutboundSmtpRelayTests
{
    private string _testDirectory = null!;
    private string _certificatePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"mk8email-relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _certificatePath = TestCertificateFactory.Create(_testDirectory, "localhost");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, recursive: true);
    }

    [TestMethod]
    public void MxResultsUsePreferenceAndImplicitFallback()
    {
        var explicitRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NoError,
            [
                ("mx2.example.com.", (ushort)20),
                ("MX1.example.com.", (ushort)10),
                ("mx1.example.com.", (ushort)30),
            ]);

        Assert.AreEqual(MailRoutingStatus.Available, explicitRoute.Status);
        CollectionAssert.AreEqual(
            new[] { "MX1.example.com", "mx2.example.com" },
            explicitRoute.Exchanges.Select(exchange => exchange.Host).ToArray());
        CollectionAssert.AreEqual(
            new ushort[] { 10, 20 },
            explicitRoute.Exchanges.Select(exchange => exchange.Preference).ToArray());

        var implicitRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NoError,
            []);

        Assert.AreEqual(MailRoutingStatus.Available, implicitRoute.Status);
        Assert.AreEqual("example.com", implicitRoute.Exchanges.Single().Host);
    }

    [TestMethod]
    public void NullMxAndNonexistentDomainStopRouting()
    {
        var nullMxRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NoError,
            [(".", (ushort)0)]);
        var nonexistentRoute = DnsMailExchangeResolver.CreateResult(
            "example.com",
            DnsHeaderResponseCode.NotExistentDomain,
            []);

        Assert.AreEqual(MailRoutingStatus.DoesNotAcceptMail, nullMxRoute.Status);
        Assert.AreEqual(0, nullMxRoute.Exchanges.Count);
        Assert.AreEqual(MailRoutingStatus.DoesNotAcceptMail, nonexistentRoute.Status);
        Assert.AreEqual(0, nonexistentRoute.Exchanges.Count);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayUsesStartTlsAndDotStuffsMessage()
    {
        await using var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: true, _certificatePath));
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            "Subject: test\r\n\r\n.first\r\nlast\r\n");
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.IsTrue(server.Session!.UsedTls);
        CollectionAssert.Contains(server.Session.DataLines, "..first");
        Assert.AreEqual(1, server.ConnectionCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelaySupportsNullReversePathForAutomaticResponses()
    {
        await using var server = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: false, certificatePath: null));
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            string.Empty,
            "recipient@example.com",
            "From: mailer-daemon@mk8n.com\r\nSubject: response\r\n\r\nbody\r\n");
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        CollectionAssert.Contains(server.Session!.Commands, "MAIL FROM:<>");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayContinuesAfterTemporaryMxFailure()
    {
        await using var firstServer = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("421 4.3.0 Try another host");
        });
        await using var secondServer = new ScriptedSmtpServer(
            session => RunSuccessfulDeliveryAsync(session, useStartTls: false, certificatePath: null));
        var relay = CreateRelay(new StubResolver(new MailRoutingResult(
            MailRoutingStatus.Available,
            [
                new MailExchangeEndpoint("localhost", 10, firstServer.Port),
                new MailExchangeEndpoint("localhost", 20, secondServer.Port),
            ])));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            "Subject: fallback\r\n\r\nbody");
        await firstServer.WaitForCompletionAsync();
        await secondServer.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.AreEqual(1, firstServer.ConnectionCount);
        Assert.AreEqual(1, secondServer.ConnectionCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDoesNotDowngradeAfterStartTlsFailure()
    {
        await using var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250-receiver.test");
            await session.WriteLineAsync("250 STARTTLS");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("220 Start TLS");
        });
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            "Subject: no downgrade\r\n\r\nbody");
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.TemporaryFailure, result.Status);
        Assert.IsFalse(server.Session!.Commands.Any(command => command.StartsWith("MAIL ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayStopsAfterPermanentRecipientFailure()
    {
        await using var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250 receiver.test");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250 Sender accepted");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("550 5.1.1 No such user");
        });
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            "Subject: reject\r\n\r\nbody");
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.IsFalse(server.Session!.Commands.Contains("DATA"));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDoesNotSendEightBitContentWithoutRemoteCapability()
    {
        await using var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250 receiver.test");
        });
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            "Subject: eight bit\r\n\r\ncafé");
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.IsFalse(server.Session!.Commands.Any(command => command.StartsWith("MAIL ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDeclaresEightBitBodyWhenRemoteSupportsIt()
    {
        await using var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250-receiver.test");
            await session.WriteLineAsync("250 8BITMIME");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250 Sender accepted");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250 Recipient accepted");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("354 Send message");
            while (await session.ReadLineAsync() is { } line && line != ".")
                session.DataLines.Add(line);
            await session.WriteLineAsync("250 Queued");
            session.Commands.Add(await session.ReadLineAsync());
        });
        var relay = CreateRelay(new StubResolver(Available(server.Port)));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            "Subject: eight bit\r\n\r\ncafé");
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        CollectionAssert.Contains(
            server.Session!.Commands,
            "MAIL FROM:<sender@mk8n.com> BODY=8BITMIME");
        CollectionAssert.Contains(server.Session.DataLines, "café");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayUsesSmtpUtf8ForInternationalizedEnvelopeAndHeaders()
    {
        await using var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250-receiver.test");
            await session.WriteLineAsync("250-8BITMIME");
            await session.WriteLineAsync("250 SMTPUTF8");
            session.Commands.Add(await session.ReadUtf8LineAsync());
            await session.WriteLineAsync("250 Sender accepted");
            session.Commands.Add(await session.ReadUtf8LineAsync());
            await session.WriteLineAsync("250 Recipient accepted");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("354 Send message");
            while (await session.ReadUtf8LineAsync() is { } line && line != ".")
                session.DataLines.Add(line);
            await session.WriteLineAsync("250 Queued");
            session.Commands.Add(await session.ReadLineAsync());
        });
        var resolver = new StubResolver(Available(server.Port));
        var relay = CreateRelay(resolver);
        const string message =
            "From: José <josé@mk8n.com>\r\n" +
            "To: δοκιμή@bücher.example\r\n" +
            "Subject: Žuta pošta\r\n\r\n" +
            "Pozdrav\r\n";
        var wireMessage = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(message));

        var result = await relay.RelayAsync(
            "josé@mk8n.com",
            "δοκιμή@bücher.example",
            wireMessage,
            new OutboundMailOptions(RequiresSmtpUtf8: true));
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.Delivered, result.Status);
        Assert.AreEqual("xn--bcher-kva.example", resolver.LastDomain);
        CollectionAssert.Contains(
            server.Session!.Commands,
            "MAIL FROM:<josé@mk8n.com> BODY=8BITMIME SMTPUTF8");
        CollectionAssert.Contains(
            server.Session.Commands,
            "RCPT TO:<δοκιμή@xn--bcher-kva.example>");
        CollectionAssert.Contains(server.Session.DataLines, "Subject: Žuta pošta");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task RelayDoesNotSendInternationalizedMessageWithoutRemoteSmtpUtf8()
    {
        await using var server = new ScriptedSmtpServer(async session =>
        {
            await session.WriteLineAsync("220 receiver.test ESMTP");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("250-receiver.test");
            await session.WriteLineAsync("250 8BITMIME");
        });
        var relay = CreateRelay(new StubResolver(Available(server.Port)));
        var wireMessage = Encoding.Latin1.GetString(
            Encoding.UTF8.GetBytes("Subject: Žuta pošta\r\n\r\nbody\r\n"));

        var result = await relay.RelayAsync(
            "sender@mk8n.com",
            "recipient@example.com",
            wireMessage,
            new OutboundMailOptions(RequiresSmtpUtf8: true));
        await server.WaitForCompletionAsync();

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.IsFalse(
            server.Session!.Commands.Any(command => command.StartsWith("MAIL ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RelayRejectsCommandInjectionBeforeDnsLookup()
    {
        var resolver = new StubResolver(Available(25));
        var relay = CreateRelay(resolver);

        var result = await relay.RelayAsync(
            "sender@mk8n.com\r\nRCPT TO:<attacker@example.com>",
            "recipient@example.com",
            "body");

        Assert.AreEqual(OutboundDeliveryStatus.PermanentFailure, result.Status);
        Assert.AreEqual(0, resolver.CallCount);
    }

    private OutboundSmtpRelay CreateRelay(IMailExchangeResolver resolver)
    {
        return new OutboundSmtpRelay(
            resolver,
            new EnvironmentConfig
            {
                Smtp = new SmtpConfig
                {
                    Hostname = "email.mk8n.com",
                },
                Limits = new LimitsConfig
                {
                    ConnectionTimeoutSeconds = 10,
                },
            },
            NullLogger<OutboundSmtpRelay>.Instance,
            (_, _, _, _) => true);
    }

    private static MailRoutingResult Available(int port) => new(
        MailRoutingStatus.Available,
        [new MailExchangeEndpoint("localhost", 10, port)]);

    private static async Task RunSuccessfulDeliveryAsync(
        SmtpTestSession session,
        bool useStartTls,
        string? certificatePath)
    {
        await session.WriteLineAsync("220 receiver.test ESMTP");
        session.Commands.Add(await session.ReadLineAsync());

        if (useStartTls)
        {
            await session.WriteLineAsync("250-receiver.test");
            await session.WriteLineAsync("250 STARTTLS");
            session.Commands.Add(await session.ReadLineAsync());
            await session.WriteLineAsync("220 Start TLS");
            await session.UpgradeToTlsAsync(certificatePath!);
            session.Commands.Add(await session.ReadLineAsync());
        }

        await session.WriteLineAsync("250 receiver.test");
        session.Commands.Add(await session.ReadLineAsync());
        await session.WriteLineAsync("250 Sender accepted");
        session.Commands.Add(await session.ReadLineAsync());
        await session.WriteLineAsync("250 Recipient accepted");
        session.Commands.Add(await session.ReadLineAsync());
        await session.WriteLineAsync("354 Send message");

        while (await session.ReadLineAsync() is { } line && line != ".")
            session.DataLines.Add(line);

        await session.WriteLineAsync("250 Queued");
        session.Commands.Add(await session.ReadLineAsync());
    }

    private sealed class StubResolver(MailRoutingResult result) : IMailExchangeResolver
    {
        public int CallCount { get; private set; }
        public string? LastDomain { get; private set; }

        public Task<MailRoutingResult> ResolveAsync(string domain, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = domain;
            return Task.FromResult(result);
        }
    }

    private sealed class ScriptedSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public ScriptedSmtpServer(Func<SmtpTestSession, Task> script)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = RunAsync(script);
        }

        public int Port { get; }
        public int ConnectionCount { get; private set; }
        public SmtpTestSession? Session { get; private set; }

        public async Task WaitForCompletionAsync()
        {
            await _serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
            _cancellation.Dispose();
        }

        private async Task RunAsync(Func<SmtpTestSession, Task> script)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            ConnectionCount++;
            await using var session = new SmtpTestSession(client.GetStream());
            Session = session;
            await script(session);
        }
    }

    private sealed class SmtpTestSession(Stream initialStream) : IAsyncDisposable
    {
        private static readonly Encoding ProtocolEncoding = Encoding.Latin1;
        private Stream _stream = initialStream;
        private StreamReader _reader = CreateReader(initialStream);
        private StreamWriter _writer = CreateWriter(initialStream);

        public bool UsedTls { get; private set; }
        public List<string> Commands { get; } = [];
        public List<string> DataLines { get; } = [];

        public async Task<string> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await _reader.ReadLineAsync(timeout.Token)
                ?? throw new EndOfStreamException("The relay closed the test connection.");
        }

        public async Task<string> ReadUtf8LineAsync()
        {
            var wireValue = await ReadLineAsync();
            return new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(Encoding.Latin1.GetBytes(wireValue));
        }

        public Task WriteLineAsync(string line) => _writer.WriteLineAsync(line);

        public async Task UpgradeToTlsAsync(string certificatePath)
        {
            await _writer.FlushAsync();
            _reader.Dispose();
            await _writer.DisposeAsync();

            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password: null);
            var tlsStream = new SslStream(_stream, leaveInnerStreamOpen: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tlsStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate },
                timeout.Token);

            _stream = tlsStream;
            _reader = CreateReader(_stream);
            _writer = CreateWriter(_stream);
            UsedTls = true;
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync();
            await _stream.DisposeAsync();
        }

        private static StreamReader CreateReader(Stream stream) =>
            new(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        private static StreamWriter CreateWriter(Stream stream) =>
            new(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };
    }
}
