using System.Data;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

public partial class ImapServerService(
IServiceScopeFactory scopeFactory,
EnvironmentConfig env,
ILogger<ImapServerService> logger) : BackgroundService
{
    private const int MaximumCommandLineCharacters = 16 * 1024;
    private const int MaximumAuthenticationLineCharacters = 4096;
    private const int MaximumConcurrentConnections = 256;
    private const int MaximumConcurrentMessageWriteCommands = 2;
    private const int MaximumConcurrentFetchCommands = 4;
    private const int MaximumConcurrentSearchCommands = 4;
    private const int MaximumSearchTokens = 4096;
    private const int MaximumSearchNestingDepth = 64;
    private const int MaximumMultiAppendMessages = 20;
    private const int MaximumCommandLiterals = 64;
    private static readonly Encoding ProtocolEncoding = MailWireEncoding.Instance;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private enum ListenerMode { Imap, ImplicitTls }

    private sealed class ImapSession
    {
        public required ListenerMode Mode { get; init; }
        public bool IsSecure { get; set; }
        public ImapState State { get; set; } = ImapState.NotAuthenticated;
        public Guid UserId { get; set; }
        public string? UserName { get; set; }
        public Guid? SelectedFolderId { get; set; }
        public string? SelectedFolderName { get; set; }
        public bool SelectedReadOnly { get; set; }
        public bool CondstoreEnabled { get; set; }
        public bool QresyncEnabled { get; set; }
        public bool CompressEnabled { get; set; }
        public required string RemoteIp { get; init; }
        public int AuthenticationFailures { get; set; }
    }

    private enum ImapState { NotAuthenticated, Authenticated, Selected, Logout }

    private enum SessionUpgrade { None, StartTls, Compress }

    private readonly record struct MessageSetRange(int Start, int End);

    private readonly ConnectionLimiter _connectionLimiter = new(MaximumConcurrentConnections);
    private readonly SemaphoreSlim _messageWriteCommandLimiter = new(
        MaximumConcurrentMessageWriteCommands,
        MaximumConcurrentMessageWriteCommands);
    private readonly SemaphoreSlim _fetchCommandLimiter = new(
        MaximumConcurrentFetchCommands,
        MaximumConcurrentFetchCommands);
    private readonly SemaphoreSlim _searchCommandLimiter = new(
        MaximumConcurrentSearchCommands,
        MaximumConcurrentSearchCommands);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = env.ToGlobalConfig();

        var tasks = new List<Task>();

        if (config.EnableImap)
            tasks.Add(ListenAsync(config.ImapPort, ListenerMode.Imap, config, stoppingToken));

        if (config.EnableImapImplicitTls)
            tasks.Add(ListenAsync(config.ImapImplicitTlsPort, ListenerMode.ImplicitTls, config, stoppingToken));

        if (tasks.Count == 0)
        {
            logger.LogWarning("No IMAP listeners are enabled.");
            return;
        }

        await Task.WhenAll(tasks);
    }

    private async Task ListenAsync(int port, ListenerMode mode, GlobalConfigDB config, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        logger.LogInformation("IMAP {Mode} listener started on port {Port}", mode, port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleConnectionAsync(client, mode, config, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            logger.LogInformation("IMAP {Mode} listener on port {Port} stopped.", mode, port);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, ListenerMode mode, GlobalConfigDB config, CancellationToken ct)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint;
        var remoteIp = (remoteEndpoint as IPEndPoint)?.Address ?? IPAddress.None;
        var remoteLabel = remoteEndpoint?.ToString() ?? "unknown";

        using var connectionLease = _connectionLimiter.TryAcquire(
            remoteIp,
            config.MaxConnectionsPerIp);
        if (connectionLease is null)
        {
            logger.LogWarning("Rejected IMAP connection from {Endpoint}: connection limit", remoteLabel);
            client.Dispose();
            return;
        }

        try
        {
            using (client)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));

                Stream stream = client.GetStream();
                SslStream? sslStream = null;

                if (mode == ListenerMode.ImplicitTls)
                {
                    if (config.TlsCertificatePath is null)
                        throw new InvalidOperationException("Implicit TLS requires a certificate.");

                    using var cert = LoadCertificate(config);
                    sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    if (!await TryAuthenticateAsServerAsync(
                            sslStream,
                            cert,
                            timeout.Token,
                            remoteLabel))
                    {
                        sslStream.Dispose();
                        return;
                    }
                    stream = sslStream;
                }

                var session = new ImapSession
                {
                    Mode = mode,
                    IsSecure = mode == ListenerMode.ImplicitTls,
                    RemoteIp = remoteIp.ToString(),
                };
                var sendGreeting = true;

                SessionUpgrade upgrade;
                do
                {
                    upgrade = await RunImapSessionAsync(stream, config, session, timeout, sendGreeting);
                    sendGreeting = false;

                    if (upgrade == SessionUpgrade.StartTls && config.TlsCertificatePath is not null)
                    {
                        using var cert = LoadCertificate(config);
                        var tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                        if (!await TryAuthenticateAsServerAsync(
                                tlsStream,
                                cert,
                                timeout.Token,
                                remoteLabel))
                        {
                            tlsStream.Dispose();
                            return;
                        }
                        stream = tlsStream;
                        session.IsSecure = true;
                    }
                    else if (upgrade == SessionUpgrade.Compress)
                    {
                        var deflateStream = new DeflateStream(stream, CompressionMode.Compress, leaveOpen: true);
                        var inflateStream = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
                        stream = new CompressedDuplexStream(inflateStream, deflateStream);
                    }
                } while (upgrade != SessionUpgrade.None && session.State != ImapState.Logout);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("IMAP connection from {Endpoint} timed out", remoteLabel);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error handling IMAP connection from {Endpoint}", remoteLabel);
        }
    }

    private async Task<SessionUpgrade> RunImapSessionAsync(
        Stream stream, GlobalConfigDB config, ImapSession session, CancellationTokenSource timeout, bool sendGreeting = true)
    {
        var ct = timeout.Token;

        using var streamReader = new StreamReader(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var reader = new BoundedLineReader(streamReader);
        await using var writer = new StreamWriter(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };

        if (sendGreeting)
            await writer.WriteLineAsync($"* OK {config.SmtpHostname} IMAP4rev1 mk8.email ready");

        while (!timeout.IsCancellationRequested && session.State != ImapState.Logout)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));
            var lineResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct);
            if (lineResult.IsTooLong)
            {
                await writer.WriteLineAsync("* BYE Command line is too long");
                session.State = ImapState.Logout;
                break;
            }

            var initialLine = lineResult.Value;
            if (initialLine is null)
                break;

            var line = await ReadCommandLiteralsAsync(
                initialLine,
                reader,
                writer,
                session,
                timeout,
                config.ConnectionTimeoutSeconds);
            if (line is null)
                continue;

            var spaceIdx = line.IndexOf(' ');
            if (spaceIdx <= 0)
            {
                await writer.WriteLineAsync("* BAD Invalid command");
                continue;
            }

            var tag = line[..spaceIdx];
            var rest = line[(spaceIdx + 1)..];

            var cmdSpaceIdx = rest.IndexOf(' ');
            var command = (cmdSpaceIdx > 0 ? rest[..cmdSpaceIdx] : rest).ToUpperInvariant();
            var args = cmdSpaceIdx > 0 ? rest[(cmdSpaceIdx + 1)..] : string.Empty;

            switch (command)
            {
                case "CAPABILITY":
                    await HandleCapabilityAsync(writer, tag, config, session);
                    break;

                case "NOOP":
                    await writer.WriteLineAsync($"{tag} OK NOOP completed");
                    break;

                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE IMAP4rev1 server logging out");
                    await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                    session.State = ImapState.Logout;
                    break;

                case "STARTTLS":
                    if (session.IsSecure)
                    {
                        await writer.WriteLineAsync($"{tag} BAD TLS is already active");
                        break;
                    }
                    if (session.State != ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} BAD STARTTLS is only available before authentication");
                        break;
                    }
                    if (config.EnableStartTls && config.TlsCertificatePath is not null)
                    {
                        await writer.WriteLineAsync($"{tag} OK Begin TLS negotiation");
                        await writer.FlushAsync(ct);
                        return SessionUpgrade.StartTls;
                    }
                    await writer.WriteLineAsync($"{tag} BAD STARTTLS not enabled");
                    break;

                case "LOGIN":
                    await HandleLoginAsync(writer, tag, args, session, ct);
                    await StopAfterTooManyAuthenticationFailuresAsync(writer, session);
                    break;

                case "AUTHENTICATE":
                    await HandleAuthenticateAsync(reader, writer, tag, args, session, ct);
                    await StopAfterTooManyAuthenticationFailuresAsync(writer, session);
                    break;

                case "NAMESPACE":
                    await writer.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL");
                    await writer.WriteLineAsync($"{tag} OK NAMESPACE completed");
                    break;

                case "ID":
                    await writer.WriteLineAsync("* ID (\"name\" \"mk8.email\" \"version\" \"1.0\")");
                    await writer.WriteLineAsync($"{tag} OK ID completed");
                    break;

                case "ENABLE":
                    if (session.State != ImapState.Authenticated)
                    {
                        await writer.WriteLineAsync($"{tag} BAD ENABLE is only valid in the authenticated state");
                        break;
                    }
                    await HandleEnableAsync(writer, tag, args, session);
                    break;

                case "SUBSCRIBE":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleSubscribeAsync(writer, tag, args, session, subscribe: true, ct);
                    break;

                case "UNSUBSCRIBE":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleSubscribeAsync(writer, tag, args, session, subscribe: false, ct);
                    break;

                case "GETQUOTAROOT":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleGetQuotaRootAsync(writer, tag, args, session, ct);
                    break;

                case "GETQUOTA":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleGetQuotaAsync(writer, tag, args, session, ct);
                    break;

                case "LIST":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleListAsync(writer, tag, args, session, ct);
                    break;

                case "LSUB":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleLsubAsync(writer, tag, args, session, ct);
                    break;

                case "SELECT":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleSelectAsync(writer, tag, args, session, readOnly: false, ct);
                    break;

                case "EXAMINE":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleSelectAsync(writer, tag, args, session, readOnly: true, ct);
                    break;

                case "CREATE":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleCreateAsync(writer, tag, args, session, ct);
                    break;

                case "DELETE":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleDeleteAsync(writer, tag, args, session, ct);
                    break;

                case "RENAME":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleRenameAsync(writer, tag, args, session, ct);
                    break;

                case "STATUS":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleStatusAsync(writer, tag, args, session, ct);
                    break;

                case "FETCH":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleFetchAsync(writer, tag, args, session, ct);
                    break;

                case "STORE":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleStoreAsync(writer, tag, args, session, ct);
                    break;

                case "SEARCH":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleSearchAsync(writer, tag, args, session, ct);
                    break;

                case "EXPUNGE":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    if (session.SelectedReadOnly)
                    {
                        await writer.WriteLineAsync($"{tag} NO Mailbox is read-only");
                        break;
                    }
                    await HandleExpungeAsync(writer, tag, session, ct);
                    break;

                case "COPY":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleCopyAsync(writer, tag, args, session, ct);
                    break;

                case "MOVE":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    if (session.SelectedReadOnly)
                    {
                        await writer.WriteLineAsync($"{tag} NO Mailbox is read-only");
                        break;
                    }
                    await HandleMoveAsync(writer, tag, args, session, ct);
                    break;

                case "APPEND":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleAppendAsync(
                        reader,
                        writer,
                        tag,
                        args,
                        session,
                        config.MaxMessageSizeBytes,
                        timeout,
                        config.ConnectionTimeoutSeconds);
                    break;

                case "IDLE":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    await HandleIdleAsync(reader, writer, tag, session, timeout, config.ConnectionTimeoutSeconds);
                    break;

                case "CHECK":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await writer.WriteLineAsync($"{tag} OK CHECK completed");
                    break;

                case "CLOSE":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    if (!session.SelectedReadOnly)
                        await ExpungeDeletedAsync(session, ct);
                    session.SelectedFolderId = null;
                    session.SelectedFolderName = null;
                    session.State = ImapState.Authenticated;
                    await writer.WriteLineAsync($"{tag} OK CLOSE completed");
                    break;

                case "UNSELECT":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    session.SelectedFolderId = null;
                    session.SelectedFolderName = null;
                    session.State = ImapState.Authenticated;
                    await writer.WriteLineAsync($"{tag} OK UNSELECT completed");
                    break;

                case "UID":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleUidAsync(writer, tag, args, session, ct);
                    break;

                case "SORT":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleSortAsync(writer, tag, args, session, useUid: false, ct);
                    break;

                case "THREAD":
                    if (session.State != ImapState.Selected)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleThreadAsync(writer, tag, args, session, useUid: false, ct);
                    break;

                case "COMPRESS":
                    if (session.State == ImapState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    if (session.CompressEnabled)
                    {
                        await writer.WriteLineAsync($"{tag} NO COMPRESS already active");
                        break;
                    }
                    if (!await HandleCompressAsync(writer, tag, args))
                        break;
                    session.CompressEnabled = true;
                    return SessionUpgrade.Compress;

                default:
                    await writer.WriteLineAsync($"{tag} BAD Command not recognized");
                    break;
            }
        }

        return SessionUpgrade.None;
    }

    private static async Task<string?> ReadCommandLiteralsAsync(
        string initialLine,
        BoundedLineReader reader,
        StreamWriter writer,
        ImapSession session,
        CancellationTokenSource timeout,
        int connectionTimeoutSeconds)
    {
        var line = initialLine;
        var tagEnd = line.IndexOf(' ');
        var tag = tagEnd > 0 ? line[..tagEnd] : "*";

        for (var literalIndex = 0; ; literalIndex++)
        {
            var match = CommandLiteralRegex().Match(line);
            if (!match.Success || IsAppendMessageLiteral(line, match.Index))
                return line;

            var isNonSynchronizing = match.Groups[2].Success;
            if (!session.IsSecure && IsCommand(line, "LOGIN"))
            {
                if (isNonSynchronizing)
                {
                    await writer.WriteLineAsync("* BYE [PRIVACYREQUIRED] TLS is required for authentication");
                    session.State = ImapState.Logout;
                }
                else
                {
                    await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] TLS is required for authentication");
                }
                return null;
            }

            if (literalIndex >= MaximumCommandLiterals
                || !int.TryParse(match.Groups[1].ValueSpan, out var literalSize)
                || literalSize > MaximumCommandLineCharacters)
            {
                await RejectCommandLiteralAsync(
                    writer,
                    tag,
                    session,
                    isNonSynchronizing,
                    "Command literal exceeds the server limit");
                return null;
            }

            if (!isNonSynchronizing)
                await writer.WriteLineAsync("+ Ready for literal data");

            var literal = new char[literalSize];
            var totalRead = 0;
            while (totalRead < literal.Length)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                var read = await reader.ReadAsync(
                    literal.AsMemory(totalRead, literal.Length - totalRead),
                    timeout.Token);
                if (read == 0)
                {
                    session.State = ImapState.Logout;
                    return null;
                }
                totalRead += read;
            }

            if (literal.AsSpan().Contains('\0'))
            {
                await writer.WriteLineAsync("* BYE Binary command literals are not supported");
                session.State = ImapState.Logout;
                return null;
            }

            timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
            var remainderResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, timeout.Token);
            if (remainderResult.IsTooLong)
            {
                await writer.WriteLineAsync("* BYE Command continuation is too long");
                session.State = ImapState.Logout;
                return null;
            }

            var remainder = remainderResult.Value;
            if (remainder is null)
            {
                session.State = ImapState.Logout;
                return null;
            }

            var escapedLiteral = EscapeImapString(new string(literal));
            var expandedLength = match.Index + escapedLiteral.Length + 2 + remainder.Length;
            if (expandedLength > MaximumCommandLineCharacters)
            {
                await writer.WriteLineAsync("* BYE Expanded command exceeds the server limit");
                session.State = ImapState.Logout;
                return null;
            }

            line = $"{line[..match.Index]}\"{escapedLiteral}\"{remainder}";
        }
    }

    private static bool IsAppendMessageLiteral(string line, int literalIndex)
    {
        if (!TryGetCommandRange(line, out _, out var commandEnd)
            || !IsCommand(line, "APPEND"))
        {
            return false;
        }

        return literalIndex > commandEnd + 1
            && !string.IsNullOrWhiteSpace(line[(commandEnd + 1)..literalIndex]);
    }

    private static bool IsCommand(string line, string expectedCommand)
    {
        if (!TryGetCommandRange(line, out var commandStart, out var commandEnd))
            return false;

        return line.AsSpan(commandStart, commandEnd - commandStart).Equals(
            expectedCommand,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCommandRange(string line, out int commandStart, out int commandEnd)
    {
        commandStart = 0;
        commandEnd = 0;
        var tagEnd = line.IndexOf(' ');
        if (tagEnd <= 0)
            return false;

        commandStart = tagEnd + 1;
        while (commandStart < line.Length && line[commandStart] == ' ')
            commandStart++;
        if (commandStart >= line.Length)
            return false;

        commandEnd = line.IndexOf(' ', commandStart);
        if (commandEnd < 0)
            commandEnd = line.Length;
        return commandEnd > commandStart;
    }

    private static async Task RejectCommandLiteralAsync(
        StreamWriter writer,
        string tag,
        ImapSession session,
        bool isNonSynchronizing,
        string response)
    {
        if (isNonSynchronizing)
        {
            await writer.WriteLineAsync($"* BYE {response}");
            session.State = ImapState.Logout;
            return;
        }

        await writer.WriteLineAsync($"{tag} BAD {response}");
    }

    private static async Task<bool> HandleCompressAsync(StreamWriter writer, string tag, string args)
    {
        var mechanism = args.Trim().ToUpperInvariant();
        if (mechanism != "DEFLATE")
        {
            await writer.WriteLineAsync($"{tag} BAD Unknown compression mechanism");
            return false;
        }

        // Signal OK — the caller will upgrade the stream
        await writer.WriteLineAsync($"{tag} OK COMPRESS DEFLATE active");
        await writer.FlushAsync();
        return true;
    }

    private static async Task HandleCapabilityAsync(
        StreamWriter writer,
        string tag,
        GlobalConfigDB config,
        ImapSession session)
    {
        var caps =
            "IMAP4rev1 LITERAL+ IDLE NAMESPACE SPECIAL-USE UIDPLUS " +
            "ID ENABLE MOVE UNSELECT QUOTA CONDSTORE QRESYNC ESEARCH";
        if (session.IsSecure)
        {
            caps += " AUTH=PLAIN SASL-IR";
        }
        else
        {
            caps += " LOGINDISABLED";
            if (config.EnableStartTls)
                caps += " STARTTLS";
        }
        await writer.WriteLineAsync($"* CAPABILITY {caps}");
        await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
    }

    private async Task HandleLoginAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] TLS is required for authentication");
            return;
        }

        if (session.State != ImapState.NotAuthenticated)
        {
            await writer.WriteLineAsync($"{tag} BAD Already authenticated");
            return;
        }

        var (username, password) = ParseLoginArgs(args);
        if (username is null || password is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error in LOGIN");
            return;
        }

        var user = await AuthenticateUserAsync(username, password, ct);
        if (user is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO LOGIN failed");
            return;
        }

        session.UserId = user.Id;
        session.UserName = user.Username;
        session.State = ImapState.Authenticated;
        await writer.WriteLineAsync($"{tag} OK LOGIN completed");
    }

    private async Task HandleAuthenticateAsync(
        BoundedLineReader reader, StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] TLS is required for authentication");
            return;
        }

        if (session.State != ImapState.NotAuthenticated)
        {
            await writer.WriteLineAsync($"{tag} BAD Already authenticated");
            return;
        }

        var authenticationArgs = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (authenticationArgs.Length == 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Missing authentication mechanism");
            return;
        }

        var mechanism = authenticationArgs[0].ToUpperInvariant();

        if (mechanism != "PLAIN")
        {
            await writer.WriteLineAsync($"{tag} NO Unsupported authentication mechanism");
            return;
        }

        string? encoded;
        if (authenticationArgs.Length == 2)
        {
            encoded = authenticationArgs[1];
            if (encoded.Length > MaximumAuthenticationLineCharacters)
            {
                await writer.WriteLineAsync($"{tag} BAD Authentication response is too long");
                return;
            }

            if (encoded == "=")
                encoded = string.Empty;
        }
        else
        {
            await writer.WriteLineAsync("+ ");
            var encodedResult = await reader.ReadLineAsync(MaximumAuthenticationLineCharacters, ct);
            encoded = encodedResult.Value;
            if (encodedResult.IsTooLong)
            {
                await writer.WriteLineAsync($"{tag} BAD Authentication response is too long");
                return;
            }
        }

        if (encoded is null || encoded == "*")
        {
            await writer.WriteLineAsync($"{tag} BAD Authentication cancelled");
            return;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(encoded));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid base64");
            return;
        }

        var firstSeparator = decoded.IndexOf('\0');
        var secondSeparator = firstSeparator < 0
            ? -1
            : decoded.IndexOf('\0', firstSeparator + 1);
        if (firstSeparator < 0 || secondSeparator < 0)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO Authentication failed");
            return;
        }

        var authorizationIdentity = decoded[..firstSeparator];
        var username = decoded[(firstSeparator + 1)..secondSeparator];
        var password = decoded[(secondSeparator + 1)..];
        if (username.Length == 0
            || authorizationIdentity.Length > 0
            && !string.Equals(authorizationIdentity, username, StringComparison.OrdinalIgnoreCase))
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO Authentication failed");
            return;
        }

        var user = await AuthenticateUserAsync(username, password, ct);
        if (user is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO Authentication failed");
            return;
        }

        session.UserId = user.Id;
        session.UserName = user.Username;
        session.State = ImapState.Authenticated;
        await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed");
    }

    private async Task<AuthenticatedMailUser?> AuthenticateUserAsync(
        string username,
        string password,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IMailAuthenticator>();
        return await authenticator.AuthenticateAsync(username, password, ct);
    }

    private void RecordAuthenticationFailure(ImapSession session)
    {
        session.AuthenticationFailures++;
        logger.LogWarning(
            "Mail authentication failed for protocol IMAP from {RemoteIp}",
            session.RemoteIp);
    }

    private static async Task StopAfterTooManyAuthenticationFailuresAsync(
        StreamWriter writer,
        ImapSession session)
    {
        if (session.AuthenticationFailures < 5)
            return;

        await writer.WriteLineAsync("* BYE Too many authentication failures");
        session.State = ImapState.Logout;
    }

    private async Task HandleListAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var returnStatus = false;
        string[] statusItems = [];
        var argsUpper = args.ToUpperInvariant();
        var returnIdx = argsUpper.IndexOf("RETURN", StringComparison.Ordinal);
        string listArgs;
        if (returnIdx >= 0)
        {
            listArgs = args[..returnIdx].Trim();
            var statusParen = argsUpper.IndexOf("STATUS", returnIdx, StringComparison.Ordinal);
            if (statusParen >= 0)
            {
                returnStatus = true;
                var openParen = args.IndexOf('(', statusParen);
                var closeParen = args.IndexOf(')', openParen + 1);
                if (openParen >= 0 && closeParen > openParen)
                    statusItems = args[(openParen + 1)..closeParen].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        else
        {
            listArgs = args;
        }

        var (reference, pattern) = ParseMailboxArgs(listArgs);

        if (pattern == string.Empty)
        {
            await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
            await writer.WriteLineAsync($"{tag} OK LIST completed");
            return;
        }

        var folders = await GetUserFoldersAsync(session.UserId, ct);

        using var scope = returnStatus ? scopeFactory.CreateScope() : null;
        var db = returnStatus ? scope!.ServiceProvider.GetRequiredService<EmailDbContext>() : null;

        foreach (var (inboxName, domain, folderName, isPrimary) in folders)
        {
            var fullName = FormatMailboxName(inboxName, domain, folderName, isPrimary);
            if (MatchesPattern(fullName, reference, pattern))
            {
                var attrs = GetFolderAttributes(folderName);
                await writer.WriteLineAsync($"* LIST ({attrs}) \"/\" \"{fullName}\"");

                if (returnStatus && db is not null && statusItems.Length > 0)
                {
                    var folder = await ResolveFolderAsync(db, session.UserId, fullName, ct);
                    if (folder is not null)
                    {
                        var statusResult = await BuildStatusResultAsync(db, folder, statusItems, ct);
                        await writer.WriteLineAsync($"* STATUS \"{fullName}\" ({statusResult})");
                    }
                }
            }
        }

        await writer.WriteLineAsync($"{tag} OK LIST completed");
    }

    private async Task HandleLsubAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var (reference, pattern) = ParseMailboxArgs(args);

        var folders = await GetUserFoldersAsync(session.UserId, ct, subscribedOnly: true);

        foreach (var (inboxName, domain, folderName, isPrimary) in folders)
        {
            var fullName = FormatMailboxName(inboxName, domain, folderName, isPrimary);
            if (MatchesPattern(fullName, reference, pattern))
            {
                var attrs = GetFolderAttributes(folderName);
                await writer.WriteLineAsync($"* LSUB ({attrs}) \"/\" \"{fullName}\"");
            }
        }

        await writer.WriteLineAsync($"{tag} OK LSUB completed");
    }

    private async Task HandleSelectAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool readOnly, CancellationToken ct)
    {
        var selectArgs = args.Trim();
        var parenIdx = selectArgs.IndexOf('(');
        int? qresyncUidValidity = null;
        long? qresyncModSeq = null;
        string? qresyncKnownUids = null;
        if (parenIdx >= 0)
        {
            var modifiers = selectArgs[parenIdx..].ToUpperInvariant();
            if (modifiers.Contains("CONDSTORE"))
                session.CondstoreEnabled = true;

            if (modifiers.Contains("QRESYNC") && session.QresyncEnabled)
            {
                session.CondstoreEnabled = true;
                var qresyncStart = selectArgs.IndexOf("QRESYNC", parenIdx, StringComparison.OrdinalIgnoreCase);
                if (qresyncStart >= 0)
                {
                    var qrOpen = selectArgs.IndexOf('(', qresyncStart);
                    var qrClose = FindMatchingParen(selectArgs, qrOpen);
                    if (qrOpen >= 0 && qrClose > qrOpen)
                    {
                        var qrParams = selectArgs[(qrOpen + 1)..qrClose].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (qrParams.Length >= 2)
                        {
                            if (int.TryParse(qrParams[0], out var uv)) qresyncUidValidity = uv;
                            if (long.TryParse(qrParams[1], out var ms)) qresyncModSeq = ms;
                        }
                        if (qrParams.Length >= 3)
                            qresyncKnownUids = qrParams[2];
                    }
                }
            }

            selectArgs = selectArgs[..parenIdx].Trim();
        }

        var mailboxName = UnquoteArg(selectArgs);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, mailboxName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found");
            return;
        }

        var totalCount = await db.Emails.CountAsync(e => e.FolderId == folder.Id, ct);
        var unseenCount = await db.Emails.CountAsync(e => e.FolderId == folder.Id && !e.IsRead, ct);

        session.SelectedFolderId = folder.Id;
        session.SelectedFolderName = mailboxName;
        session.SelectedReadOnly = readOnly;
        session.State = ImapState.Selected;

        await writer.WriteLineAsync($"* {totalCount} EXISTS");
        await writer.WriteLineAsync("* 0 RECENT");
        await writer.WriteLineAsync("* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)");
        await writer.WriteLineAsync("* OK [PERMANENTFLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft \\*)] Flags permitted");
        await writer.WriteLineAsync($"* OK [UIDVALIDITY {folder.UidValidity}]");
        await writer.WriteLineAsync($"* OK [UIDNEXT {folder.NextUid}]");
        await writer.WriteLineAsync($"* OK [HIGHESTMODSEQ {folder.HighestModSeq}]");
        await writer.WriteLineAsync($"* OK [MAILBOXID ({folder.MailboxId})]");

        if (unseenCount > 0)
        {
            var allIds = await db.Emails
                .Where(e => e.FolderId == folder.Id)
                .OrderBy(e => e.Uid)
                .Select(e => new { e.Id, e.IsRead })
                .ToListAsync(ct);

            var firstUnseenIdx = allIds.FindIndex(e => !e.IsRead);
            if (firstUnseenIdx >= 0)
                await writer.WriteLineAsync($"* OK [UNSEEN {firstUnseenIdx + 1}]");
        }

        // QRESYNC: send VANISHED and changed flags since the requested modseq
        if (qresyncUidValidity is not null && qresyncModSeq is not null
            && qresyncUidValidity.Value == folder.UidValidity)
        {
            // Report expunged UIDs since qresyncModSeq
            var vanished = await db.ExpungedUids
                .Where(eu => eu.FolderId == folder.Id && eu.ModSeq > qresyncModSeq.Value)
                .OrderBy(eu => eu.Uid)
                .Select(eu => eu.Uid)
                .ToListAsync(ct);

            if (vanished.Count > 0)
            {
                var vanishedSet = FormatUidRange(vanished);
                await writer.WriteLineAsync($"* VANISHED (EARLIER) {vanishedSet}");
            }

            // Report changed messages since qresyncModSeq
            var changed = await SelectEmailMetadata(
                    db.Emails.Where(e => e.FolderId == folder.Id && e.ModSeq > qresyncModSeq.Value))
                .OrderBy(e => e.Uid)
                .ToListAsync(ct);

            var allEmailIds = await db.Emails
                .Where(e => e.FolderId == folder.Id)
                .OrderBy(e => e.Uid)
                .Select(e => e.Id)
                .ToListAsync(ct);

            foreach (var email in changed)
            {
                var seqIdx = allEmailIds.IndexOf(email.Id);
                if (seqIdx >= 0)
                {
                    var seqNum = seqIdx + 1;
                    var flags = BuildFlagsList(email);
                    await writer.WriteLineAsync($"* {seqNum} FETCH (UID {email.Uid} FLAGS ({flags}) MODSEQ ({email.ModSeq}))");
                }
            }
        }

        var cmdName = readOnly ? "EXAMINE" : "SELECT";
        var access = readOnly ? "[READ-ONLY]" : "[READ-WRITE]";
        await writer.WriteLineAsync($"{tag} OK {access} {cmdName} completed");
    }

    private static int FindMatchingParen(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '(')
            return -1;
        var depth = 0;
        for (var i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static string FormatUidRange(List<int> uids)
    {
        if (uids.Count == 0) return "";
        var sb = new StringBuilder();
        var start = uids[0];
        var end = uids[0];
        for (var i = 1; i < uids.Count; i++)
        {
            if (uids[i] == end + 1)
            {
                end = uids[i];
            }
            else
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(start == end ? $"{start}" : $"{start}:{end}");
                start = end = uids[i];
            }
        }
        if (sb.Length > 0) sb.Append(',');
        sb.Append(start == end ? $"{start}" : $"{start}:{end}");
        return sb.ToString();
    }

    private async Task HandleCreateAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var mailboxName = UnquoteArg(args.Trim());
        var parts = mailboxName.Split('/');

        if (parts.Length < 3)
        {
            await writer.WriteLineAsync($"{tag} NO Invalid mailbox name");
            return;
        }

        var inboxLocal = parts[0];
        var domain = parts[1];
        var folderName = string.Join("/", parts[2..]);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var inbox = await db.Inboxes.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Name == inboxLocal
                                   && i.Address.Domain == domain
                                   && i.OwnerId == session.UserId, ct);
        if (inbox is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found");
            return;
        }

        var exists = await db.Folders.AnyAsync(f => f.InboxId == inbox.Id && f.Name == folderName, ct);
        if (exists)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox already exists");
            return;
        }

        db.Folders.Add(new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = folderName,
            InboxId = inbox.Id,
        });
        await db.SaveChangesAsync(ct);

        await writer.WriteLineAsync($"{tag} OK CREATE completed");
    }

    private async Task HandleDeleteAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var mailboxName = UnquoteArg(args.Trim());

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, mailboxName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found");
            return;
        }

        db.Folders.Remove(folder);
        await db.SaveChangesAsync(ct);

        if (session.SelectedFolderId == folder.Id)
        {
            session.SelectedFolderId = null;
            session.SelectedFolderName = null;
            session.State = ImapState.Authenticated;
        }

        await writer.WriteLineAsync($"{tag} OK DELETE completed");
    }

    private async Task HandleRenameAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var parsedArgs = ParseTwoMailboxArgs(args);
        if (parsedArgs is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var (oldName, newName) = parsedArgs.Value;
        var newParts = newName.Split('/');
        if (newParts.Length < 3)
        {
            await writer.WriteLineAsync($"{tag} NO Invalid new mailbox name");
            return;
        }

        var newFolderName = string.Join("/", newParts[2..]);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, oldName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found");
            return;
        }

        folder.Name = newFolderName;
        await db.SaveChangesAsync(ct);

        await writer.WriteLineAsync($"{tag} OK RENAME completed");
    }

    private async Task HandleStatusAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var parenIdx = args.IndexOf('(');
        if (parenIdx < 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var mailboxName = UnquoteArg(args[..parenIdx].Trim());
        var statusItemsRaw = args[(parenIdx + 1)..].TrimEnd(')').Trim();
        var statusItems = statusItemsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, mailboxName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found");
            return;
        }

        var statusResult = await BuildStatusResultAsync(db, folder, statusItems, ct);

        await writer.WriteLineAsync($"* STATUS \"{mailboxName}\" ({statusResult})");
        await writer.WriteLineAsync($"{tag} OK STATUS completed");
    }

    private static async Task<string> BuildStatusResultAsync(
        EmailDbContext db, FolderDB folder, string[] statusItems, CancellationToken ct)
    {
        int? totalCount = null;
        int? unseenCount = null;

        var results = new StringBuilder();
        foreach (var item in statusItems)
        {
            if (results.Length > 0) results.Append(' ');
            switch (item.ToUpperInvariant())
            {
                case "MESSAGES":
                    totalCount ??= await db.Emails.CountAsync(e => e.FolderId == folder.Id, ct);
                    results.Append($"MESSAGES {totalCount}");
                    break;
                case "RECENT":
                    results.Append("RECENT 0");
                    break;
                case "UNSEEN":
                    unseenCount ??= await db.Emails.CountAsync(e => e.FolderId == folder.Id && !e.IsRead, ct);
                    results.Append($"UNSEEN {unseenCount}");
                    break;
                case "UIDVALIDITY":
                    results.Append($"UIDVALIDITY {folder.UidValidity}");
                    break;
                case "UIDNEXT":
                    results.Append($"UIDNEXT {folder.NextUid}");
                    break;
                case "HIGHESTMODSEQ":
                    results.Append($"HIGHESTMODSEQ {folder.HighestModSeq}");
                    break;
                case "MAILBOXID":
                    results.Append($"MAILBOXID ({folder.MailboxId})");
                    break;
            }
        }

        return results.ToString();
    }

    private async Task HandleFetchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleFetchWithLimitAsync(writer, tag, args, session, useUid: false, ct);
    }

    private async Task HandleFetchWithLimitAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        if (!_fetchCommandLimiter.Wait(0))
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent FETCH commands");
            return;
        }

        try
        {
            await HandleFetchCoreAsync(writer, tag, args, session, useUid, ct);
        }
        finally
        {
            _fetchCommandLimiter.Release();
        }
    }

    private async Task HandleFetchCoreAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var messageSet = args[..spaceIdx];
        var fetchItems = args[(spaceIdx + 1)..].Trim().TrimStart('(').TrimEnd(')');
        var implicitSeen = ShouldSetSeen(fetchItems);

        if (fetchItems.Contains("MODSEQ", StringComparison.OrdinalIgnoreCase))
            session.CondstoreEnabled = true;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var folderId = session.SelectedFolderId!.Value;
        var messageQuery = db.Emails.AsNoTracking().Where(email => email.FolderId == folderId);
        var maximumIdentifier = useUid
            ? await messageQuery.MaxAsync(email => (int?)email.Uid, ct) ?? 0
            : await messageQuery.CountAsync(ct);
        if (!TryParseMessageSet(messageSet, maximumIdentifier, out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var includeStoredContent = FetchNeedsStoredContent(fetchItems);
        var fetchQuery = CreateFetchQuery(messageQuery, includeStoredContent);
        var seenUpdates = new List<EmailDB>();
        var folder = implicitSeen && !session.SelectedReadOnly
            ? await db.Folders.FindAsync([folderId], ct)
            : null;
        var sequenceNumber = 0;

        await foreach (var email in fetchQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            sequenceNumber++;
            var identifier = useUid ? email.Uid : sequenceNumber;
            if (!MessageSetContains(parsedMessageSet, identifier))
                continue;

            if (implicitSeen && !email.IsRead && folder is not null)
            {
                email.IsRead = true;
                email.ModSeq = ++folder.HighestModSeq;
                seenUpdates.Add(new EmailDB
                {
                    Id = email.Id,
                    IsRead = true,
                    ModSeq = email.ModSeq,
                });
            }

            var response = BuildFetchResponse(sequenceNumber, email, fetchItems, useUid);
            await writer.WriteLineAsync(response);
        }

        foreach (var update in seenUpdates)
        {
            db.Emails.Attach(update);
            db.Entry(update).Property(email => email.IsRead).IsModified = true;
            db.Entry(update).Property(email => email.ModSeq).IsModified = true;
        }

        if (seenUpdates.Count > 0)
            await db.SaveChangesAsync(ct);

        var commandName = useUid ? "UID FETCH" : "FETCH";
        await writer.WriteLineAsync($"{tag} OK {commandName} completed");
    }

    private async Task HandleStoreAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (session.SelectedReadOnly)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox is read-only");
            return;
        }

        var (sequenceSet, unchangedSince, action, flagsRaw) = ParseStoreArgs(args);
        if (sequenceSet is null || action is null || flagsRaw is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        if (unchangedSince is not null)
            session.CondstoreEnabled = true;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var emails = await GetEmailMetadataInFolderAsync(db, session.SelectedFolderId!.Value, ct);
        if (!TryParseMessageSet(sequenceSet, emails.Count, out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var selected = emails
            .Select((email, index) => (Email: email, SequenceNumber: index + 1))
            .Where(item => MessageSetContains(parsedMessageSet, item.SequenceNumber))
            .ToList();
        var flagsList = flagsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var isSilent = action.Contains(".SILENT");

        var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
        var newModSeq = ++folder!.HighestModSeq;

        var modified = new List<int>();

        foreach (var item in selected)
        {
            var email = item.Email;
            var seqNum = item.SequenceNumber;

            if (unchangedSince is not null && email.ModSeq > unchangedSince.Value)
            {
                modified.Add(seqNum);
                continue;
            }

            var tracked = AttachFlagUpdate(db, email, action, flagsList, newModSeq);

            if (!isSilent)
            {
                var flags = BuildFlagsList(tracked);
                if (session.CondstoreEnabled)
                    await writer.WriteLineAsync($"* {seqNum} FETCH (FLAGS ({flags}) MODSEQ ({newModSeq}))");
                else
                    await writer.WriteLineAsync($"* {seqNum} FETCH (FLAGS ({flags}))");
            }
        }

        await db.SaveChangesAsync(ct);

        if (modified.Count > 0)
        {
            var modifiedSet = string.Join(',', modified);
            await writer.WriteLineAsync($"{tag} OK [MODIFIED {modifiedSet}] STORE completed");
        }
        else
        {
            await writer.WriteLineAsync($"{tag} OK STORE completed");
        }
    }

    private async Task HandleSearchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleSearchWithLimitAsync(writer, tag, args, session, useUid: false, ct);
    }

    private async Task HandleSearchWithLimitAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        if (!_searchCommandLimiter.Wait(0))
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent SEARCH commands");
            return;
        }

        try
        {
            await HandleSearchCoreAsync(writer, tag, args, session, useUid, ct);
        }
        finally
        {
            _searchCommandLimiter.Release();
        }
    }

    private async Task HandleSearchCoreAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var (returnOptions, searchCriteria) = ParseEsearchReturn(args);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var query = db.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == session.SelectedFolderId!.Value);
        var searchResult = await FindSearchCandidatesAsync(query, searchCriteria.Trim(), ct);
        if (searchResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}");
            return;
        }

        var numbers = searchResult.Matches
            .Select(candidate => useUid ? candidate.Uid : candidate.SequenceNumber)
            .ToList();
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        if (returnOptions is not null)
        {
            var result = BuildEsearchResult(returnOptions, numbers);
            var uidMarker = useUid ? " UID" : string.Empty;
            await writer.WriteLineAsync($"* ESEARCH (TAG \"{tag}\"){uidMarker} {result}");
        }
        else
        {
            await writer.WriteLineAsync($"* SEARCH {string.Join(' ', numbers)}");
        }

        var commandName = useUid ? "UID SEARCH" : "SEARCH";
        await writer.WriteLineAsync($"{tag} OK {commandName} completed");
    }

    private async Task HandleExpungeAsync(StreamWriter writer, string tag, ImapSession session, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var emails = await GetEmailMetadataInFolderAsync(
            db,
            session.SelectedFolderId!.Value,
            ct);

        var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
        var expunged = 0;
        var vanishedUids = new List<int>();

        for (var i = 0; i < emails.Count; i++)
        {
            if (IsMarkedDeleted(emails[i]))
            {
                var expungeModSeq = ++folder!.HighestModSeq;

                if (session.QresyncEnabled)
                {
                    vanishedUids.Add(emails[i].Uid);
                }
                else
                {
                    var seqNum = i + 1 - expunged;
                    await writer.WriteLineAsync($"* {seqNum} EXPUNGE");
                }

                db.ExpungedUids.Add(new ExpungedUidDB
                {
                    Id = Guid.CreateVersion7(),
                    Uid = emails[i].Uid,
                    ModSeq = expungeModSeq,
                    FolderId = session.SelectedFolderId!.Value,
                });

                AttachDelete(db, emails[i]);
                expunged++;
            }
        }

        if (session.QresyncEnabled && vanishedUids.Count > 0)
        {
            var vanishedSet = FormatUidRange(vanishedUids);
            await writer.WriteLineAsync($"* VANISHED {vanishedSet}");
        }

        await db.SaveChangesAsync(ct);
        await writer.WriteLineAsync($"{tag} OK EXPUNGE completed");
    }

    private async Task HandleCopyAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleCopyWithLimitAsync(writer, tag, args, session, useUid: false, ct);
    }

    private async Task HandleCopyWithLimitAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        if (!_messageWriteCommandLimiter.Wait(0))
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent message writes");
            return;
        }

        try
        {
            await HandleCopyCoreAsync(writer, tag, args, session, useUid, ct);
        }
        finally
        {
            _messageWriteCommandLimiter.Release();
        }
    }

    private async Task HandleCopyCoreAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var messageSet = args[..spaceIdx];
        var destMailbox = UnquoteArg(args[(spaceIdx + 1)..].Trim());

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        var destFolder = await ResolveFolderAsync(db, session.UserId, destMailbox, ct);
        if (destFolder is null)
        {
            await writer.WriteLineAsync($"{tag} NO [TRYCREATE] Destination mailbox not found");
            return;
        }

        var emails = await GetEmailMetadataInFolderAsync(db, session.SelectedFolderId!.Value, ct);
        var maximumIdentifier = useUid
            ? (emails.Count > 0 ? emails[^1].Uid : 0)
            : emails.Count;
        if (!TryParseMessageSet(messageSet, maximumIdentifier, out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var selected = emails
            .Where((email, index) => MessageSetContains(
                parsedMessageSet,
                useUid ? email.Uid : index + 1))
            .ToList();

        if (!TryGetTotalStoredSize(selected, out var addedBytes))
        {
            await writer.WriteLineAsync($"{tag} NO [SERVERBUG] COPY source size is invalid");
            return;
        }

        if (selected.Count > 0
            && !await HasUserQuotaCapacityAsync(db, session.UserId, addedBytes, ct))
        {
            await writer.WriteLineAsync($"{tag} NO [OVERQUOTA] COPY exceeds the mailbox quota");
            return;
        }

        var (srcUids, dstUids) = await CopyMessagesAsync(db, destFolder, selected, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var commandName = useUid ? "UID COPY" : "COPY";
        await writer.WriteLineAsync(
            $"{tag} OK [COPYUID {destFolder.UidValidity} {FormatUidSet(srcUids)} {FormatUidSet(dstUids)}] {commandName} completed");
    }

    private async Task HandleUidAsync(
        StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var subCommand = args[..spaceIdx].ToUpperInvariant();
        var subArgs = args[(spaceIdx + 1)..];

        switch (subCommand)
        {
            case "FETCH":
                await HandleUidFetchAsync(writer, tag, subArgs, session, ct);
                break;
            case "SEARCH":
                await HandleUidSearchAsync(writer, tag, subArgs, session, ct);
                break;
            case "STORE":
                await HandleUidStoreAsync(writer, tag, subArgs, session, ct);
                break;
            case "COPY":
                await HandleUidCopyAsync(writer, tag, subArgs, session, ct);
                break;
            case "MOVE":
                if (session.SelectedReadOnly)
                {
                    await writer.WriteLineAsync($"{tag} NO Mailbox is read-only");
                    break;
                }
                await HandleUidMoveAsync(writer, tag, subArgs, session, ct);
                break;
            case "SORT":
                await HandleSortAsync(writer, tag, subArgs, session, useUid: true, ct);
                break;
            case "THREAD":
                await HandleThreadAsync(writer, tag, subArgs, session, useUid: true, ct);
                break;
            case "EXPUNGE":
                if (session.SelectedReadOnly)
                {
                    await writer.WriteLineAsync($"{tag} NO Mailbox is read-only");
                    break;
                }
                await HandleUidExpungeAsync(writer, tag, subArgs, session, ct);
                break;
            default:
                await writer.WriteLineAsync($"{tag} BAD Unknown UID command");
                break;
        }
    }

    private async Task HandleUidFetchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleFetchWithLimitAsync(writer, tag, args, session, useUid: true, ct);
    }

    private async Task HandleUidSearchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleSearchWithLimitAsync(writer, tag, args, session, useUid: true, ct);
    }

    private async Task HandleUidStoreAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (session.SelectedReadOnly)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox is read-only");
            return;
        }

        var (storeUidSet, unchangedSince, action, flagsRaw) = ParseStoreArgs(args);
        if (storeUidSet is null || action is null || flagsRaw is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        if (unchangedSince is not null)
            session.CondstoreEnabled = true;

        var flagsList = flagsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var emails = await GetEmailMetadataInFolderAsync(db, session.SelectedFolderId!.Value, ct);
        var maxUid = emails.Count > 0 ? emails[^1].Uid : 0;
        if (!TryParseMessageSet(storeUidSet, maxUid, out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var isSilent = action.Contains(".SILENT");

        var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
        var newModSeq = ++folder!.HighestModSeq;

        var modified = new List<int>();

        for (var i = 0; i < emails.Count; i++)
        {
            var email = emails[i];
            if (!MessageSetContains(parsedMessageSet, email.Uid)) continue;

            if (unchangedSince is not null && email.ModSeq > unchangedSince.Value)
            {
                modified.Add(email.Uid);
                continue;
            }

            var tracked = AttachFlagUpdate(db, email, action, flagsList, newModSeq);

            if (!isSilent)
            {
                var seqNum = i + 1;
                var flags = BuildFlagsList(tracked);
                if (session.CondstoreEnabled)
                    await writer.WriteLineAsync($"* {seqNum} FETCH (UID {email.Uid} FLAGS ({flags}) MODSEQ ({newModSeq}))");
                else
                    await writer.WriteLineAsync($"* {seqNum} FETCH (UID {email.Uid} FLAGS ({flags}))");
            }
        }

        await db.SaveChangesAsync(ct);

        if (modified.Count > 0)
        {
            var modifiedSet = string.Join(',', modified);
            await writer.WriteLineAsync($"{tag} OK [MODIFIED {modifiedSet}] UID STORE completed");
        }
        else
        {
            await writer.WriteLineAsync($"{tag} OK UID STORE completed");
        }
    }

    private async Task HandleUidCopyAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleCopyWithLimitAsync(writer, tag, args, session, useUid: true, ct);
    }

    private static async Task HandleEnableAsync(StreamWriter writer, string tag, string args, ImapSession session)
    {
        var requested = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var enabled = new List<string>();

        foreach (var ext in requested)
        {
            if (ext.Equals("CONDSTORE", StringComparison.OrdinalIgnoreCase))
            {
                session.CondstoreEnabled = true;
                enabled.Add("CONDSTORE");
            }
            else if (ext.Equals("QRESYNC", StringComparison.OrdinalIgnoreCase))
            {
                session.QresyncEnabled = true;
                session.CondstoreEnabled = true;
                enabled.Add("QRESYNC");
            }
        }

        var enabledStr = enabled.Count > 0 ? string.Join(' ', enabled) : "";
        await writer.WriteLineAsync($"* ENABLED {enabledStr}");
        await writer.WriteLineAsync($"{tag} OK ENABLE completed");
    }

    private async Task HandleSubscribeAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool subscribe, CancellationToken ct)
    {
        var mailboxName = UnquoteArg(args.Trim());

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, mailboxName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found");
            return;
        }

        folder.IsSubscribed = subscribe;
        await db.SaveChangesAsync(ct);

        var cmd = subscribe ? "SUBSCRIBE" : "UNSUBSCRIBE";
        await writer.WriteLineAsync($"{tag} OK {cmd} completed");
    }

    // ?? Helpers ??

    private async Task<List<(string InboxName, string Domain, string FolderName, bool IsPrimary)>> GetUserFoldersAsync(
        Guid userId, CancellationToken ct, bool subscribedOnly = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var query = db.Folders
            .AsNoTracking()
            .Where(f => f.Inbox.OwnerId == userId);

        if (subscribedOnly)
            query = query.Where(f => f.IsSubscribed);

        var folders = await query
            .Select(f => new
            {
                InboxName = f.Inbox.Name,
                f.Inbox.Address.Domain,
                FolderName = f.Name,
                OwnerUsername = f.Inbox.Owner.Username,
            })
            .OrderBy(f => f.Domain).ThenBy(f => f.InboxName).ThenBy(f => f.FolderName)
            .ToListAsync(ct);

        return folders
            .Select(folder => ValueTuple.Create(
                folder.InboxName,
                folder.Domain,
                folder.FolderName,
                string.Equals(
                    folder.OwnerUsername,
                    $"{folder.InboxName}@{folder.Domain}",
                    StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static async Task<FolderDB?> ResolveFolderAsync(EmailDbContext db, Guid userId, string mailboxName, CancellationToken ct)
    {
        if (!mailboxName.Contains('/'))
        {
            var username = await db.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.Username)
                .SingleOrDefaultAsync(ct);
            if (username is null)
                return null;

            var separator = username.LastIndexOf('@');
            if (separator <= 0 || separator == username.Length - 1)
                return null;

            var primaryLocalPart = username[..separator];
            var primaryDomain = username[(separator + 1)..];
            var primaryFolderName = NormalizePrimaryFolderName(mailboxName);
            return await db.Folders
                .FirstOrDefaultAsync(f => f.Inbox.Name == primaryLocalPart
                                       && f.Inbox.Address.Domain == primaryDomain
                                       && f.Inbox.OwnerId == userId
                                       && f.Name == primaryFolderName, ct);
        }

        var parts = mailboxName.Split('/', 3);
        if (parts.Length < 3)
            return null;

        var inboxLocal = parts[0];
        var domain = parts[1];
        var folderName = parts[2];

        return await db.Folders
            .FirstOrDefaultAsync(f => f.Inbox.Name == inboxLocal
                                   && f.Inbox.Address.Domain == domain
                                   && f.Inbox.OwnerId == userId
                                   && f.Name == folderName, ct);
    }

    private static IQueryable<EmailDB> SelectEmailMetadata(IQueryable<EmailDB> query) =>
        query
            .AsNoTracking()
            .Select(email => new EmailDB
            {
                Id = email.Id,
                IsRead = email.IsRead,
                IsDeleted = email.IsDeleted,
                IsFlagged = email.IsFlagged,
                IsDraft = email.IsDraft,
                IsAnswered = email.IsAnswered,
                ModSeq = email.ModSeq,
                Uid = email.Uid,
                SizeBytes = email.SizeBytes,
                FolderId = email.FolderId,
            });

    private static async Task<List<EmailDB>> GetEmailMetadataInFolderAsync(
        EmailDbContext db,
        Guid folderId,
        CancellationToken ct)
    {
        return await SelectEmailMetadata(db.Emails.Where(email => email.FolderId == folderId))
            .OrderBy(email => email.Uid)
            .ToListAsync(ct);
    }

    private static bool TryGetTotalStoredSize(IReadOnlyList<EmailDB> emails, out long totalBytes)
    {
        totalBytes = 0;
        foreach (var email in emails)
        {
            if (email.SizeBytes < 0 || totalBytes > long.MaxValue - email.SizeBytes)
            {
                totalBytes = 0;
                return false;
            }

            totalBytes += email.SizeBytes;
        }

        return true;
    }

    private static async Task<(List<int> SourceUids, List<int> DestinationUids)> CopyMessagesAsync(
        EmailDbContext db,
        FolderDB destinationFolder,
        IReadOnlyList<EmailDB> metadata,
        CancellationToken ct)
    {
        var sourceUids = new List<int>(metadata.Count);
        var destinationUids = new List<int>(metadata.Count);

        foreach (var item in metadata)
        {
            var source = await db.Emails
                .AsNoTracking()
                .SingleAsync(email => email.Id == item.Id && email.FolderId == item.FolderId, ct);
            var newUid = destinationFolder.NextUid++;
            var newModSeq = ++destinationFolder.HighestModSeq;
            var copy = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = source.Sender,
                Recipient = source.Recipient,
                Subject = source.Subject,
                Body = source.Body,
                RawHeaders = source.RawHeaders,
                SizeBytes = source.SizeBytes,
                MessageId = source.MessageId,
                InReplyTo = source.InReplyTo,
                Cc = source.Cc,
                EmailObjectId = Guid.CreateVersion7().ToString("N"),
                ThreadObjectId = source.ThreadObjectId,
                IsRead = source.IsRead,
                IsDeleted = false,
                IsFlagged = source.IsFlagged,
                IsDraft = source.IsDraft,
                IsAnswered = source.IsAnswered,
                ReceivedAt = source.ReceivedAt,
                Uid = newUid,
                ModSeq = newModSeq,
                FolderId = destinationFolder.Id,
            };

            sourceUids.Add(source.Uid);
            destinationUids.Add(newUid);
            db.Emails.Add(copy);
            await db.SaveChangesAsync(ct);
            db.Entry(copy).State = EntityState.Detached;
        }

        return (sourceUids, destinationUids);
    }

    private static EmailDB AttachFlagUpdate(
        EmailDbContext db,
        EmailDB metadata,
        string action,
        string[] flags,
        long modSeq)
    {
        var update = new EmailDB
        {
            Id = metadata.Id,
            IsRead = metadata.IsRead,
            IsDeleted = metadata.IsDeleted,
            IsFlagged = metadata.IsFlagged,
            IsDraft = metadata.IsDraft,
            IsAnswered = metadata.IsAnswered,
            ModSeq = modSeq,
        };
        ApplyFlags(update, action, flags);
        db.Emails.Attach(update);
        var entry = db.Entry(update);
        entry.Property(email => email.IsRead).IsModified = true;
        entry.Property(email => email.IsDeleted).IsModified = true;
        entry.Property(email => email.IsFlagged).IsModified = true;
        entry.Property(email => email.IsDraft).IsModified = true;
        entry.Property(email => email.IsAnswered).IsModified = true;
        entry.Property(email => email.ModSeq).IsModified = true;
        return update;
    }

    private static EmailDB AttachMoveUpdate(
        EmailDbContext db,
        EmailDB metadata,
        Guid destinationFolderId,
        int destinationUid,
        long destinationModSeq)
    {
        var update = new EmailDB
        {
            Id = metadata.Id,
            FolderId = metadata.FolderId,
            Uid = metadata.Uid,
            ModSeq = metadata.ModSeq,
        };
        db.Emails.Attach(update);
        update.FolderId = destinationFolderId;
        update.Uid = destinationUid;
        update.ModSeq = destinationModSeq;
        var entry = db.Entry(update);
        entry.Property(email => email.FolderId).IsModified = true;
        entry.Property(email => email.Uid).IsModified = true;
        entry.Property(email => email.ModSeq).IsModified = true;
        return update;
    }

    private static void AttachDelete(EmailDbContext db, EmailDB metadata)
    {
        var update = new EmailDB { Id = metadata.Id };
        db.Emails.Attach(update);
        db.Emails.Remove(update);
    }

    private async Task ExpungeDeletedAsync(ImapSession session, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var deleted = await GetEmailMetadataInFolderAsync(
            db,
            session.SelectedFolderId!.Value,
            ct);

        var toRemove = deleted.Where(IsMarkedDeleted).ToList();
        if (toRemove.Count > 0)
        {
            var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
            foreach (var email in toRemove)
            {
                var expungeModSeq = ++folder!.HighestModSeq;
                db.ExpungedUids.Add(new ExpungedUidDB
                {
                    Id = Guid.CreateVersion7(),
                    Uid = email.Uid,
                    ModSeq = expungeModSeq,
                    FolderId = session.SelectedFolderId!.Value,
                });
            }
            foreach (var email in toRemove)
                AttachDelete(db, email);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string FormatMailboxName(
        string inboxName,
        string domain,
        string folderName,
        bool isPrimary)
    {
        if (!isPrimary)
            return $"{inboxName}/{domain}/{folderName}";

        return string.Equals(folderName, DefaultFolders.Inbox, StringComparison.OrdinalIgnoreCase)
            ? "INBOX"
            : folderName;
    }

    private static string NormalizePrimaryFolderName(string folderName)
    {
        if (string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase))
            return DefaultFolders.Inbox;

        return DefaultFolders.All.FirstOrDefault(
            name => string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)) ?? folderName;
    }

    private static string FormatUidSet(List<int> uids) =>
        uids.Count > 0 ? string.Join(',', uids) : "0";

    private static string GenerateThreadObjectId(string? inReplyTo, string? messageId)
    {
        // Thread ID is a stable hash of the In-Reply-To if present, otherwise a new GUID
        if (!string.IsNullOrEmpty(inReplyTo))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(inReplyTo));
            return Convert.ToHexStringLower(hash[..16]);
        }

        if (!string.IsNullOrEmpty(messageId))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(messageId));
            return Convert.ToHexStringLower(hash[..16]);
        }

        return Guid.CreateVersion7().ToString("N");
    }

    private static X509Certificate2 LoadCertificate(GlobalConfigDB config)
    {
        if (config.TlsCertificateKeyPath is not null)
            return X509Certificate2.CreateFromPemFile(config.TlsCertificatePath!, config.TlsCertificateKeyPath);
        return X509CertificateLoader.LoadPkcs12FromFile(config.TlsCertificatePath!, password: null);
    }

    private async Task<bool> TryAuthenticateAsServerAsync(
        SslStream stream,
        X509Certificate2 certificate,
        CancellationToken cancellationToken,
        string remoteLabel)
    {
        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or AuthenticationException or SocketException)
        {
            logger.LogDebug(
                "IMAP TLS handshake from {Endpoint} ended before authentication: {ExceptionType}",
                remoteLabel,
                exception.GetType().Name);
            return false;
        }
    }

    private static bool ShouldSetSeen(string fetchItems)
    {
        var upper = fetchItems.ToUpperInvariant();
        if (upper.Contains("BODY.PEEK") || upper.Contains("BINARY.PEEK"))
            return false;
        if (IsFetchMacro(upper, "RFC822"))
            return true;
        if (upper.Contains("BINARY["))
            return true;
        if (!upper.Contains("BODY["))
            return false;
        if (upper.Contains("BODY[HEADER"))
            return false;
        return true;
    }

    private static bool FetchNeedsStoredContent(string fetchItems)
    {
        var normalized = fetchItems
            .ToUpperInvariant()
            .Replace("BODY.PEEK[", "BODY[");
        return normalized.Contains("BODY[", StringComparison.Ordinal)
            || normalized.Contains("BODYSTRUCTURE", StringComparison.Ordinal)
            || BodyStandaloneRegex().IsMatch(normalized)
            || IsFetchMacro(normalized, "RFC822")
            || normalized.Contains("RFC822.HEADER", StringComparison.Ordinal)
            || normalized.Contains("RFC822.TEXT", StringComparison.Ordinal)
            || BinaryFetchRegex().IsMatch(normalized)
            || BinarySizeRegex().IsMatch(normalized);
    }

    private static IQueryable<EmailDB> CreateFetchQuery(
        IQueryable<EmailDB> messageQuery,
        bool includeStoredContent)
    {
        if (includeStoredContent)
            return messageQuery.OrderBy(email => email.Uid);

        return messageQuery
            .Select(email => new EmailDB
            {
                Id = email.Id,
                Sender = email.Sender,
                Recipient = email.Recipient,
                Subject = email.Subject,
                Body = email.SizeBytes > 0 ? string.Empty : email.Body,
                IsRead = email.IsRead,
                IsDeleted = email.IsDeleted,
                IsFlagged = email.IsFlagged,
                IsDraft = email.IsDraft,
                IsAnswered = email.IsAnswered,
                ModSeq = email.ModSeq,
                Uid = email.Uid,
                EmailObjectId = email.EmailObjectId,
                ThreadObjectId = email.ThreadObjectId,
                SizeBytes = email.SizeBytes,
                RawHeaders = email.SizeBytes > 0 ? null : email.RawHeaders,
                MessageId = email.MessageId,
                InReplyTo = email.InReplyTo,
                Cc = email.Cc,
                ReceivedAt = email.ReceivedAt,
                FolderId = email.FolderId,
            })
            .OrderBy(email => email.Uid);
    }

    private static bool IsFetchMacro(string items, string macro) =>
        items == macro || items.StartsWith(macro + " ") || items.EndsWith(" " + macro) || items.Contains(" " + macro + " ");

    private static string GetFolderAttributes(string folderName)
    {
        return folderName switch
        {
            "Inbox" => "\\HasNoChildren",
            "Sent" => "\\Sent \\HasNoChildren",
            "Drafts" => "\\Drafts \\HasNoChildren",
            "Trash" => "\\Trash \\HasNoChildren",
            "Spam" => "\\Junk \\HasNoChildren",
            _ => "\\HasNoChildren",
        };
    }

    private static bool MatchesPattern(string name, string reference, string pattern)
    {
        var fullPattern = reference + pattern;
        if (fullPattern == "*")
            return true;
        if (fullPattern == "%")
            return !name.Contains('/');

        var regexPattern = "^" + Regex.Escape(fullPattern)
            .Replace("\\*", ".*")
            .Replace("%", "[^/]*") + "$";

        return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase);
    }

    private static string BuildFetchResponse(int seqNum, EmailDB email, string fetchItems, bool useUid)
    {
        var items = fetchItems.ToUpperInvariant();
        var normalizedItems = items.Replace("BODY.PEEK[", "BODY[");
        var parts = new List<string>();

        var partialMatch = PartialFetchRegex().Match(items);
        int? partialOffset = null;
        int? partialCount = null;
        if (partialMatch.Success)
        {
            partialOffset = int.Parse(partialMatch.Groups[1].Value);
            partialCount = int.Parse(partialMatch.Groups[2].Value);
        }

        var isMacroAll = IsFetchMacro(normalizedItems, "ALL");
        var isMacroFast = IsFetchMacro(normalizedItems, "FAST");
        var isMacroFull = IsFetchMacro(normalizedItems, "FULL");

        if (normalizedItems.Contains("FLAGS") || isMacroAll || isMacroFast || isMacroFull)
            parts.Add($"FLAGS ({BuildFlagsList(email)})");

        if (normalizedItems.Contains("INTERNALDATE") || isMacroAll || isMacroFast || isMacroFull)
            parts.Add($"INTERNALDATE \"{email.ReceivedAt:dd-MMM-yyyy HH:mm:ss} +0000\"");

        if (normalizedItems.Contains("RFC822.SIZE") || isMacroAll || isMacroFast || isMacroFull)
        {
            var size = email.SizeBytes > 0 ? email.SizeBytes : MailWireEncoding.Instance.GetByteCount(BuildRfc822(email));
            parts.Add($"RFC822.SIZE {size}");
        }

        if (normalizedItems.Contains("ENVELOPE") || isMacroAll || isMacroFull)
        {
            parts.Add($"ENVELOPE {BuildEnvelope(email)}");
        }

        if (normalizedItems.Contains("BODY[]") || IsFetchMacro(normalizedItems, "RFC822"))
        {
            var rfc822 = BuildRfc822(email);
            var (data, origin) = ApplyPartial(rfc822, partialOffset, partialCount);
            var suffix = origin is not null ? $"<{origin}>" : "";
            parts.Add($"BODY[]{suffix} {{{MailWireEncoding.Instance.GetByteCount(data)}}}\r\n{data}");
        }

        if (normalizedItems.Contains("BODY[HEADER]") || normalizedItems.Contains("RFC822.HEADER"))
        {
            var header = BuildRfc822Header(email);
            parts.Add($"BODY[HEADER] {{{MailWireEncoding.Instance.GetByteCount(header)}}}\r\n{header}");
        }

        if (normalizedItems.Contains("BODY[TEXT]") || normalizedItems.Contains("RFC822.TEXT"))
        {
            var body = email.Body;
            parts.Add($"BODY[TEXT] {{{MailWireEncoding.Instance.GetByteCount(body)}}}\r\n{body}");
        }

        var headerFieldsMatch = HeaderFieldsRegex().Match(items);
        if (headerFieldsMatch.Success)
        {
            var requestedFields = headerFieldsMatch.Groups[1].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filtered = FilterHeaders(email, requestedFields);
            parts.Add($"BODY[HEADER.FIELDS ({headerFieldsMatch.Groups[1].Value})] {{{MailWireEncoding.Instance.GetByteCount(filtered)}}}\r\n{filtered}");
        }

        var headerFieldsNotMatch = HeaderFieldsNotRegex().Match(items);
        if (headerFieldsNotMatch.Success)
        {
            var excludedFields = headerFieldsNotMatch.Groups[1].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filtered = FilterHeadersNot(email, excludedFields);
            parts.Add($"BODY[HEADER.FIELDS.NOT ({headerFieldsNotMatch.Groups[1].Value})] {{{MailWireEncoding.Instance.GetByteCount(filtered)}}}\r\n{filtered}");
        }

        if (normalizedItems.Contains("BODYSTRUCTURE"))
        {
            var size = MailWireEncoding.Instance.GetByteCount(email.Body);
            var lines = email.Body.Split('\n').Length;
            parts.Add($"BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" {size} {lines})");
        }
        else if (BodyStandaloneRegex().IsMatch(normalizedItems))
        {
            var size = MailWireEncoding.Instance.GetByteCount(email.Body);
            var lines = email.Body.Split('\n').Length;
            parts.Add($"BODY (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" {size} {lines})");
        }

        var sectionMatch = BodySectionRegex().Match(items);
        if (sectionMatch.Success)
        {
            var section = sectionMatch.Groups[1].Value;
            var sectionUpper = section.ToUpperInvariant();
            if (sectionUpper is "" or "TEXT" or "1")
            {
                // already handled above for BODY[] and BODY[TEXT]
                if (sectionUpper == "1")
                {
                    var bodyContent = email.Body;
                    parts.Add($"BODY[1] {{{MailWireEncoding.Instance.GetByteCount(bodyContent)}}}\r\n{bodyContent}");
                }
            }
            else if (sectionUpper == "1.MIME")
            {
                var mime = "Content-Type: text/plain; charset=UTF-8\r\n\r\n";
                parts.Add($"BODY[1.MIME] {{{MailWireEncoding.Instance.GetByteCount(mime)}}}\r\n{mime}");
            }
        }

        if (normalizedItems.Contains("MODSEQ"))
            parts.Add($"MODSEQ ({email.ModSeq})");

        if (normalizedItems.Contains("EMAILID"))
        {
            var emailId = email.EmailObjectId ?? email.Id.ToString("N");
            parts.Add($"EMAILID ({emailId})");
        }

        if (normalizedItems.Contains("THREADID"))
        {
            var threadId = email.ThreadObjectId;
            parts.Add(threadId is not null ? $"THREADID ({threadId})" : "THREADID NIL");
        }

        // BINARY extension (RFC 3516)
        if (BinaryFetchRegex().IsMatch(items))
        {
            var binaryMatch = BinaryFetchRegex().Match(items);
            var binarySection = binaryMatch.Groups[1].Value;
            var content = binarySection.ToUpperInvariant() switch
            {
                "" or "1" => email.Body,
                "HEADER" => BuildRfc822Header(email),
                "TEXT" => email.Body,
                _ => email.Body,
            };
            var contentBytes = MailWireEncoding.Instance.GetBytes(content);
            parts.Add($"BINARY[{binarySection}] ~{{{contentBytes.Length}}}\r\n{content}");
        }

        if (BinarySizeRegex().IsMatch(items))
        {
            var sizeMatch = BinarySizeRegex().Match(items);
            var sizeSection = sizeMatch.Groups[1].Value;
            var content = sizeSection.ToUpperInvariant() switch
            {
                "" or "1" => email.Body,
                _ => email.Body,
            };
            parts.Add($"BINARY.SIZE[{sizeSection}] {MailWireEncoding.Instance.GetByteCount(content)}");
        }

        if (useUid || normalizedItems.Contains("UID"))
            parts.Add($"UID {email.Uid}");

        return $"* {seqNum} FETCH ({string.Join(' ', parts)})";
    }

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[HEADER\.FIELDS\s*\(([^)]+)\)\]")]
    private static partial Regex HeaderFieldsRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[HEADER\.FIELDS\.NOT\s*\(([^)]+)\)\]")]
    private static partial Regex HeaderFieldsNotRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[(\d[\d.]*(?:\.MIME)?)\]")]
    private static partial Regex BodySectionRegex();

    [GeneratedRegex(@"<(\d+)\.(\d+)>")]
    private static partial Regex PartialFetchRegex();

    [GeneratedRegex(@"(?<![.\[A-Z])BODY(?![.\[A-Z])")]
    private static partial Regex BodyStandaloneRegex();

    [GeneratedRegex(@"BINARY(?:\.PEEK)?\[([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BinaryFetchRegex();

    [GeneratedRegex(@"BINARY\.SIZE\[([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BinarySizeRegex();

    private static (string data, int? origin) ApplyPartial(string content, int? offset, int? count)
    {
        if (offset is null || count is null)
            return (content, null);

        var bytes = MailWireEncoding.Instance.GetBytes(content);
        var start = Math.Min(offset.Value, bytes.Length);
        var length = Math.Min(count.Value, bytes.Length - start);
        var sliced = MailWireEncoding.Instance.GetString(bytes, start, length);
        return (sliced, start);
    }

    private static string BuildFlagsList(EmailDB email)
    {
        var flags = new List<string>();
        if (email.IsRead) flags.Add("\\Seen");
        if (email.IsDeleted) flags.Add("\\Deleted");
        if (email.IsFlagged) flags.Add("\\Flagged");
        if (email.IsDraft) flags.Add("\\Draft");
        if (email.IsAnswered) flags.Add("\\Answered");
        return string.Join(' ', flags);
    }

    private static string BuildRfc822(EmailDB email)
    {
        if (email.RawHeaders is not null)
            return email.RawHeaders + "\r\n\r\n" + email.Body;

        var sb = new StringBuilder();
        sb.Append($"From: {email.Sender}\r\n");
        sb.Append($"To: {email.Recipient}\r\n");
        if (!string.IsNullOrEmpty(email.Cc))
            sb.Append($"Cc: {email.Cc}\r\n");
        sb.Append($"Subject: {email.Subject}\r\n");
        sb.Append($"Date: {email.ReceivedAt:ddd, dd MMM yyyy HH:mm:ss +0000}\r\n");
        if (!string.IsNullOrEmpty(email.MessageId))
            sb.Append($"Message-ID: {email.MessageId}\r\n");
        if (!string.IsNullOrEmpty(email.InReplyTo))
            sb.Append($"In-Reply-To: {email.InReplyTo}\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        sb.Append("\r\n");
        sb.Append(email.Body);
        return sb.ToString();
    }

    private static string BuildRfc822Header(EmailDB email)
    {
        if (email.RawHeaders is not null)
            return email.RawHeaders + "\r\n\r\n";

        var sb = new StringBuilder();
        sb.Append($"From: {email.Sender}\r\n");
        sb.Append($"To: {email.Recipient}\r\n");
        if (!string.IsNullOrEmpty(email.Cc))
            sb.Append($"Cc: {email.Cc}\r\n");
        sb.Append($"Subject: {email.Subject}\r\n");
        sb.Append($"Date: {email.ReceivedAt:ddd, dd MMM yyyy HH:mm:ss +0000}\r\n");
        if (!string.IsNullOrEmpty(email.MessageId))
            sb.Append($"Message-ID: {email.MessageId}\r\n");
        if (!string.IsNullOrEmpty(email.InReplyTo))
            sb.Append($"In-Reply-To: {email.InReplyTo}\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static void ApplyFlags(EmailDB email, string action, string[] flags)
    {
        bool SetValue(bool current, string act) => act switch
        {
            "+FLAGS" or "+FLAGS.SILENT" => true,
            "-FLAGS" or "-FLAGS.SILENT" => false,
            "FLAGS" or "FLAGS.SILENT" => true,
            _ => current,
        };

        if (action is "FLAGS" or "FLAGS.SILENT")
        {
            email.IsRead = false;
            email.IsDeleted = false;
            email.IsFlagged = false;
            email.IsDraft = false;
            email.IsAnswered = false;
        }

        foreach (var flag in flags)
        {
            switch (flag.ToUpperInvariant())
            {
                case "\\SEEN":
                    email.IsRead = SetValue(email.IsRead, action);
                    break;
                case "\\DELETED":
                    email.IsDeleted = SetValue(email.IsDeleted, action);
                    break;
                case "\\FLAGGED":
                    email.IsFlagged = SetValue(email.IsFlagged, action);
                    break;
                case "\\DRAFT":
                    email.IsDraft = SetValue(email.IsDraft, action);
                    break;
                case "\\ANSWERED":
                    email.IsAnswered = SetValue(email.IsAnswered, action);
                    break;
            }
        }
    }

    private static bool IsMarkedDeleted(EmailDB email) => email.IsDeleted;

    private static bool TryParseMessageSet(
        string value,
        int maximumIdentifier,
        out List<MessageSetRange> ranges)
    {
        ranges = [];
        if (string.IsNullOrEmpty(value))
            return false;

        var parsedRanges = new List<MessageSetRange>();
        foreach (var part in value.Split(','))
        {
            if (part.Length == 0)
                return false;

            var separator = part.IndexOf(':');
            int start;
            int end;
            if (separator >= 0)
            {
                if (separator == 0
                    || separator == part.Length - 1
                    || part.IndexOf(':', separator + 1) >= 0
                    || !TryParseMessageSetEndpoint(part[..separator], maximumIdentifier, out start)
                    || !TryParseMessageSetEndpoint(part[(separator + 1)..], maximumIdentifier, out end))
                {
                    return false;
                }
            }
            else if (TryParseMessageSetEndpoint(part, maximumIdentifier, out start))
            {
                end = start;
            }
            else
            {
                return false;
            }

            if (start > end)
                (start, end) = (end, start);
            parsedRanges.Add(new MessageSetRange(start, end));
        }

        parsedRanges.Sort(static (left, right) =>
        {
            var startComparison = left.Start.CompareTo(right.Start);
            return startComparison != 0 ? startComparison : left.End.CompareTo(right.End);
        });

        foreach (var range in parsedRanges)
        {
            if (ranges.Count == 0)
            {
                ranges.Add(range);
                continue;
            }

            var previous = ranges[^1];
            var adjacent = previous.End < int.MaxValue && range.Start == previous.End + 1;
            if (range.Start <= previous.End || adjacent)
            {
                ranges[^1] = new MessageSetRange(previous.Start, Math.Max(previous.End, range.End));
            }
            else
            {
                ranges.Add(range);
            }
        }

        return true;
    }

    private static bool TryParseMessageSetEndpoint(
        string value,
        int maximumIdentifier,
        out int identifier)
    {
        if (value == "*")
        {
            identifier = maximumIdentifier;
            return true;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                identifier = 0;
                return false;
            }
        }

        return int.TryParse(value, out identifier) && identifier > 0;
    }

    private static bool MessageSetContains(IReadOnlyList<MessageSetRange> ranges, int identifier)
    {
        var low = 0;
        var high = ranges.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var range = ranges[middle];
            if (identifier < range.Start)
            {
                high = middle - 1;
            }
            else if (identifier > range.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static (string? username, string? password) ParseLoginArgs(string args)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 2) return (null, null);
        return (UnquoteArg(tokens[0]), UnquoteArg(tokens[1]));
    }

    private static (string reference, string pattern) ParseMailboxArgs(string args)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 2) return (string.Empty, string.Empty);
        return (UnquoteArg(tokens[0]), UnquoteArg(tokens[1]));
    }

    private static (string oldName, string newName)? ParseTwoMailboxArgs(string args)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 2) return null;
        return (UnquoteArg(tokens[0]), UnquoteArg(tokens[1]));
    }

    private static List<string> ParseImapTokens(string input)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && input[i] == ' ') i++;
            if (i >= input.Length) break;

            if (input[i] == '"')
            {
                i++;
                var token = new StringBuilder();
                while (i < input.Length)
                {
                    var character = input[i++];
                    if (character == '"')
                        break;
                    if (character == '\\'
                        && i < input.Length
                        && input[i] is '\\' or '"')
                    {
                        character = input[i++];
                    }
                    token.Append(character);
                }
                tokens.Add(token.ToString());
            }
            else
            {
                var end = input.IndexOf(' ', i);
                if (end < 0) end = input.Length;
                tokens.Add(input[i..end]);
                i = end;
            }
        }
        return tokens;
    }

    private static string UnquoteArg(string arg)
    {
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
        {
            var result = new StringBuilder(arg.Length - 2);
            for (var index = 1; index < arg.Length - 1; index++)
            {
                var character = arg[index];
                if (character == '\\'
                    && index + 1 < arg.Length - 1
                    && arg[index + 1] is '\\' or '"')
                {
                    character = arg[++index];
                }
                result.Append(character);
            }
            return result.ToString();
        }
        return arg;
    }

    private static string EscapeImapString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static (string? sequenceSet, long? unchangedSince, string? action, string? flagsRaw) ParseStoreArgs(string args)
    {
        var parts = args.Split(' ', 3);
        if (parts.Length < 3)
            return (null, null, null, null);

        var sequenceSet = parts[0];

        if (parts[1].StartsWith('('))
        {
            var rest = parts[1] + " " + parts[2];
            var closeParenIdx = rest.IndexOf(')');
            if (closeParenIdx < 0)
                return (null, null, null, null);

            var modifier = rest[1..closeParenIdx];
            var modParts = modifier.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            long? unchangedSince = null;
            if (modParts.Length == 2 &&
                modParts[0].Equals("UNCHANGEDSINCE", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(modParts[1], out var modSeq))
            {
                unchangedSince = modSeq;
            }

            var remaining = rest[(closeParenIdx + 1)..].Trim();
            var remParts = remaining.Split(' ', 2);
            if (remParts.Length < 2)
                return (null, null, null, null);

            var action = remParts[0].ToUpperInvariant();
            var flagsRaw = remParts[1].Trim().TrimStart('(').TrimEnd(')');
            return (sequenceSet, unchangedSince, action, flagsRaw);
        }
        else
        {
            var action = parts[1].ToUpperInvariant();
            var flagsRaw = parts[2].Trim().TrimStart('(').TrimEnd(')');
            return (sequenceSet, null, action, flagsRaw);
        }
    }

    private async Task HandleGetQuotaRootAsync(
        StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var mailboxName = UnquoteArg(args.Trim());

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        if (await ResolveFolderAsync(db, session.UserId, mailboxName, ct) is null)
        {
            await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Mailbox not found");
            return;
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.UserId, ct);

        var usedBytes = await db.Emails
            .Where(e => e.Folder.Inbox.OwnerId == session.UserId)
            .SumAsync(e => (long)e.SizeBytes, ct);

        var quotaBytes = user?.QuotaBytes ?? 0;

        await writer.WriteLineAsync($"* QUOTAROOT \"{EscapeImapString(mailboxName)}\" \"\"");
        if (quotaBytes > 0)
            await writer.WriteLineAsync(
                $"* QUOTA \"\" (STORAGE {ToQuotaStorageUnits(usedBytes)} {ToQuotaStorageUnits(quotaBytes)})");
        else
            await writer.WriteLineAsync("* QUOTA \"\" ()");
        await writer.WriteLineAsync($"{tag} OK GETQUOTAROOT completed");
    }

    private async Task HandleGetQuotaAsync(
        StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!string.Equals(UnquoteArg(args.Trim()), string.Empty, StringComparison.Ordinal))
        {
            await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Quota root not found");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.UserId, ct);

        var usedBytes = await db.Emails
            .Where(e => e.Folder.Inbox.OwnerId == session.UserId)
            .SumAsync(e => (long)e.SizeBytes, ct);

        var quotaBytes = user?.QuotaBytes ?? 0;

        if (quotaBytes > 0)
            await writer.WriteLineAsync(
                $"* QUOTA \"\" (STORAGE {ToQuotaStorageUnits(usedBytes)} {ToQuotaStorageUnits(quotaBytes)})");
        else
            await writer.WriteLineAsync("* QUOTA \"\" ()");
        await writer.WriteLineAsync($"{tag} OK GETQUOTA completed");
    }

    private static long ToQuotaStorageUnits(long bytes) =>
        bytes <= 0 ? 0 : 1 + (bytes - 1) / 1024;

    private static string BuildEnvelope(EmailDB email)
    {
        var (senderLocal, senderDomain) = SplitAddress(email.Sender);
        var (rcptLocal, rcptDomain) = SplitAddress(email.Recipient);

        var date = email.ReceivedAt.ToString("ddd, dd MMM yyyy HH:mm:ss +0000");

        var inReplyTo = string.IsNullOrEmpty(email.InReplyTo)
            ? "NIL"
            : $"\"{EscapeImapString(email.InReplyTo)}\"";
        var messageId = string.IsNullOrEmpty(email.MessageId)
            ? "NIL"
            : $"\"{EscapeImapString(email.MessageId)}\"";

        var cc = "NIL";
        if (!string.IsNullOrEmpty(email.Cc))
        {
            var (ccLocal, ccDomain) = SplitAddress(email.Cc);
            cc = $"((\"{EscapeImapString(email.Cc)}\" NIL \"{ccLocal}\" \"{ccDomain}\"))";
        }

        return $"(\"{date}\" \"{EscapeImapString(email.Subject)}\" " +
               $"((\"{EscapeImapString(email.Sender)}\" NIL \"{senderLocal}\" \"{senderDomain}\")) " +
               $"((\"{EscapeImapString(email.Sender)}\" NIL \"{senderLocal}\" \"{senderDomain}\")) " +
               $"((\"{EscapeImapString(email.Sender)}\" NIL \"{senderLocal}\" \"{senderDomain}\")) " +
               $"((\"{EscapeImapString(email.Recipient)}\" NIL \"{rcptLocal}\" \"{rcptDomain}\")) " +
               $"{cc} NIL {inReplyTo} {messageId})";
    }

    private static (string local, string domain) SplitAddress(string address)
    {
        var atIdx = address.IndexOf('@');
        return atIdx >= 0
            ? (address[..atIdx], address[(atIdx + 1)..])
            : (address, string.Empty);
    }

    private static string FilterHeaders(EmailDB email, string[] requestedFields)
    {
        var headers = email.RawHeaders ?? BuildRfc822Header(email);
        var sb = new StringBuilder();

        var headerLines = headers.Split('\n');
        var include = false;

        foreach (var rawLine in headerLines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                if (include)
                    sb.Append(line).Append("\r\n");
                continue;
            }

            include = false;
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var fieldName = line[..colonIdx].Trim();
                foreach (var req in requestedFields)
                {
                    if (string.Equals(fieldName, req, StringComparison.OrdinalIgnoreCase))
                    {
                        include = true;
                        break;
                    }
                }
            }

            if (include)
                sb.Append(line).Append("\r\n");
        }

        sb.Append("\r\n");
        return sb.ToString();
    }

    private static string FilterHeadersNot(EmailDB email, string[] excludedFields)
    {
        var headers = email.RawHeaders ?? BuildRfc822Header(email);
        var sb = new StringBuilder();

        var headerLines = headers.Split('\n');
        var include = true;

        foreach (var rawLine in headerLines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                if (include)
                    sb.Append(line).Append("\r\n");
                continue;
            }

            include = true;
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var fieldName = line[..colonIdx].Trim();
                foreach (var excl in excludedFields)
                {
                    if (string.Equals(fieldName, excl, StringComparison.OrdinalIgnoreCase))
                    {
                        include = false;
                        break;
                    }
                }
            }

            if (include)
                sb.Append(line).Append("\r\n");
        }

        sb.Append("\r\n");
        return sb.ToString();
    }

    [Flags]
    private enum SearchDataRequirements
    {
        None = 0,
        Body = 1,
        RawHeaders = 2,
    }

    private enum SearchTokenKind
    {
        Atom,
        OpenParenthesis,
        CloseParenthesis,
    }

    private readonly record struct SearchToken(SearchTokenKind Kind, string Value);

    private sealed record SearchStoredMessage(
        Guid Id,
        int Uid,
        string Sender,
        string Recipient,
        string Subject,
        string Body,
        bool IsRead,
        bool IsDeleted,
        bool IsFlagged,
        bool IsDraft,
        bool IsAnswered,
        long ModSeq,
        int SizeBytes,
        string? RawHeaders,
        string? MessageId,
        string? InReplyTo,
        string? Cc,
        DateTime ReceivedAt);

    private sealed record SearchCandidate(Guid Id, int Uid, int SequenceNumber);

    private sealed record SearchExecutionResult(
        IReadOnlyList<SearchCandidate> Matches,
        string? FailureResponse);

    private sealed record SearchPredicate(
        SearchDataRequirements Requirements,
        Func<SearchStoredMessage, int, bool> IsMatch);

    private sealed class SearchParser(
        IReadOnlyList<SearchToken> tokens,
        int maximumSequenceNumber,
        int maximumUid)
    {
        private int _index;
        private string _failureResponse = "BAD Invalid search criteria";

        public bool TryParse(out SearchPredicate predicate, out string failureResponse)
        {
            predicate = MatchNothing();

            if (tokens.Count == 0)
            {
                failureResponse = _failureResponse;
                return false;
            }

            if (CurrentAtomEquals("CHARSET"))
            {
                _index++;
                if (!TryReadValue(out var charset))
                {
                    failureResponse = _failureResponse;
                    return false;
                }

                if (!charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase))
                {
                    failureResponse = "NO [BADCHARSET (US-ASCII)] Unsupported search charset";
                    return false;
                }
            }

            if (_index >= tokens.Count
                || !TryParseConjunction(stopAtCloseParenthesis: false, depth: 0, out predicate)
                || _index != tokens.Count)
            {
                failureResponse = _failureResponse;
                return false;
            }

            failureResponse = string.Empty;
            return true;
        }

        private bool TryParseConjunction(
            bool stopAtCloseParenthesis,
            int depth,
            out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (depth > MaximumSearchNestingDepth)
                return false;

            var predicates = new List<SearchPredicate>();
            while (_index < tokens.Count
                   && tokens[_index].Kind != SearchTokenKind.CloseParenthesis)
            {
                if (!TryParseKey(depth, out var item))
                    return false;
                predicates.Add(item);
            }

            if (predicates.Count == 0)
                return false;

            if (stopAtCloseParenthesis)
            {
                if (_index >= tokens.Count
                    || tokens[_index].Kind != SearchTokenKind.CloseParenthesis)
                {
                    return false;
                }

                _index++;
            }
            else if (_index < tokens.Count)
            {
                return false;
            }

            predicate = CombineAnd(predicates);
            return true;
        }

        private bool TryParseKey(int depth, out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (depth > MaximumSearchNestingDepth || _index >= tokens.Count)
                return false;

            var token = tokens[_index++];
            if (token.Kind == SearchTokenKind.OpenParenthesis)
            {
                return TryParseConjunction(
                    stopAtCloseParenthesis: true,
                    depth + 1,
                    out predicate);
            }

            if (token.Kind != SearchTokenKind.Atom)
                return false;

            if (LooksLikeMessageSet(token.Value))
            {
                if (!TryParseMessageSet(
                        token.Value,
                        maximumSequenceNumber,
                        out var sequenceRanges))
                {
                    return false;
                }

                predicate = new SearchPredicate(
                    SearchDataRequirements.None,
                    (_, sequenceNumber) => MessageSetContains(sequenceRanges, sequenceNumber));
                return true;
            }

            switch (token.Value.ToUpperInvariant())
            {
                case "ALL":
                    predicate = MatchEverything();
                    return true;
                case "ANSWERED":
                    predicate = Flag(static message => message.IsAnswered);
                    return true;
                case "UNANSWERED":
                    predicate = Flag(static message => !message.IsAnswered);
                    return true;
                case "DELETED":
                    predicate = Flag(static message => message.IsDeleted);
                    return true;
                case "UNDELETED":
                    predicate = Flag(static message => !message.IsDeleted);
                    return true;
                case "DRAFT":
                    predicate = Flag(static message => message.IsDraft);
                    return true;
                case "UNDRAFT":
                    predicate = Flag(static message => !message.IsDraft);
                    return true;
                case "FLAGGED":
                    predicate = Flag(static message => message.IsFlagged);
                    return true;
                case "UNFLAGGED":
                    predicate = Flag(static message => !message.IsFlagged);
                    return true;
                case "SEEN":
                    predicate = Flag(static message => message.IsRead);
                    return true;
                case "UNSEEN":
                    predicate = Flag(static message => !message.IsRead);
                    return true;
                case "NEW":
                case "RECENT":
                    predicate = MatchNothing();
                    return true;
                case "OLD":
                    predicate = MatchEverything();
                    return true;
                case "NOT":
                    if (!TryParseKey(depth + 1, out var negated))
                        return false;
                    predicate = new SearchPredicate(
                        negated.Requirements,
                        (message, sequenceNumber) => !negated.IsMatch(message, sequenceNumber));
                    return true;
                case "OR":
                    if (!TryParseKey(depth + 1, out var left)
                        || !TryParseKey(depth + 1, out var right))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        left.Requirements | right.Requirements,
                        (message, sequenceNumber) => left.IsMatch(message, sequenceNumber)
                            || right.IsMatch(message, sequenceNumber));
                    return true;
                case "BCC":
                    return TryParseHeaderValue("Bcc", out predicate);
                case "CC":
                    return TryParseHeaderValue("Cc", out predicate);
                case "FROM":
                    return TryParseHeaderValue("From", out predicate);
                case "SUBJECT":
                    if (!TryReadValue(out var subject))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => ContainsSearchText(message.Subject, subject));
                    return true;
                case "TO":
                    return TryParseHeaderValue("To", out predicate);
                case "BODY":
                    if (!TryReadValue(out var bodyText))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.Body,
                        (message, _) => ContainsSearchText(message.Body, bodyText));
                    return true;
                case "TEXT":
                    if (!TryReadValue(out var text))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.Body | SearchDataRequirements.RawHeaders,
                        (message, _) => MessageTextContains(message, text));
                    return true;
                case "HEADER":
                    if (!TryReadValue(out var headerName)
                        || headerName.Length == 0
                        || !TryReadValue(out var headerValue))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.RawHeaders,
                        (message, _) => HeaderContains(
                            message.RawHeaders,
                            headerName,
                            headerValue));
                    return true;
                case "BEFORE":
                    return TryParseReceivedDate(
                        static (messageDate, searchDate) => messageDate < searchDate,
                        out predicate);
                case "ON":
                    return TryParseReceivedDate(
                        static (messageDate, searchDate) => messageDate == searchDate,
                        out predicate);
                case "SINCE":
                    return TryParseReceivedDate(
                        static (messageDate, searchDate) => messageDate >= searchDate,
                        out predicate);
                case "SENTBEFORE":
                    return TryParseSentDate(
                        static (messageDate, searchDate) => messageDate < searchDate,
                        out predicate);
                case "SENTON":
                    return TryParseSentDate(
                        static (messageDate, searchDate) => messageDate == searchDate,
                        out predicate);
                case "SENTSINCE":
                    return TryParseSentDate(
                        static (messageDate, searchDate) => messageDate >= searchDate,
                        out predicate);
                case "LARGER":
                    if (!TryReadUnsignedNumber(out var larger))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => message.SizeBytes > larger);
                    return true;
                case "SMALLER":
                    if (!TryReadUnsignedNumber(out var smaller))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => message.SizeBytes < smaller);
                    return true;
                case "UID":
                    if (!TryReadValue(out var uidSet)
                        || !TryParseMessageSet(uidSet, maximumUid, out var uidRanges))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => MessageSetContains(uidRanges, message.Uid));
                    return true;
                case "KEYWORD":
                    if (!TryReadValue(out var keyword))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => HasKeyword(message, keyword));
                    return true;
                case "UNKEYWORD":
                    if (!TryReadValue(out var absentKeyword))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => !HasKeyword(message, absentKeyword));
                    return true;
                case "MODSEQ":
                    if (!TryReadValue(out var modSequenceText)
                        || !long.TryParse(
                            modSequenceText,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var modSequence)
                        || modSequence <= 0)
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => message.ModSeq >= modSequence);
                    return true;
                default:
                    return false;
            }
        }

        private bool TryParseHeaderValue(string headerName, out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (!TryReadValue(out var value))
                return false;

            predicate = new SearchPredicate(
                SearchDataRequirements.RawHeaders,
                (message, _) => HeaderContains(message.RawHeaders, headerName, value));
            return true;
        }

        private bool TryParseReceivedDate(
            Func<DateOnly, DateOnly, bool> comparison,
            out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (!TryReadValue(out var value)
                || !TryParseImapDate(value, out var parsedDate))
            {
                return false;
            }

            var searchDate = DateOnly.FromDateTime(parsedDate);
            predicate = new SearchPredicate(
                SearchDataRequirements.None,
                (message, _) => comparison(
                    DateOnly.FromDateTime(message.ReceivedAt),
                    searchDate));
            return true;
        }

        private bool TryParseSentDate(
            Func<DateOnly, DateOnly, bool> comparison,
            out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (!TryReadValue(out var value)
                || !TryParseImapDate(value, out var parsedDate))
            {
                return false;
            }

            var searchDate = DateOnly.FromDateTime(parsedDate);
            predicate = new SearchPredicate(
                SearchDataRequirements.RawHeaders,
                (message, _) => TryGetSentDate(message.RawHeaders, out var sentDate)
                    && comparison(sentDate, searchDate));
            return true;
        }

        private bool TryReadUnsignedNumber(out long value)
        {
            value = 0;
            return TryReadValue(out var text)
                && long.TryParse(
                    text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value)
                && value >= 0
                && value <= uint.MaxValue;
        }

        private bool TryReadValue(out string value)
        {
            value = string.Empty;
            if (_index >= tokens.Count || tokens[_index].Kind != SearchTokenKind.Atom)
                return false;

            value = tokens[_index++].Value;
            return true;
        }

        private bool CurrentAtomEquals(string value) =>
            _index < tokens.Count
            && tokens[_index].Kind == SearchTokenKind.Atom
            && tokens[_index].Value.Equals(value, StringComparison.OrdinalIgnoreCase);

        private static SearchPredicate CombineAnd(IReadOnlyList<SearchPredicate> predicates)
        {
            if (predicates.Count == 1)
                return predicates[0];

            var requirements = SearchDataRequirements.None;
            foreach (var predicate in predicates)
                requirements |= predicate.Requirements;

            return new SearchPredicate(
                requirements,
                (message, sequenceNumber) =>
                {
                    foreach (var predicate in predicates)
                    {
                        if (!predicate.IsMatch(message, sequenceNumber))
                            return false;
                    }

                    return true;
                });
        }

        private static SearchPredicate Flag(Func<SearchStoredMessage, bool> predicate) =>
            new(SearchDataRequirements.None, (message, _) => predicate(message));

        private static SearchPredicate MatchEverything() =>
            new(SearchDataRequirements.None, static (_, _) => true);

        private static SearchPredicate MatchNothing() =>
            new(SearchDataRequirements.None, static (_, _) => false);
    }

    private static async Task<SearchExecutionResult> FindSearchCandidatesAsync(
        IQueryable<EmailDB> query,
        string criteria,
        CancellationToken cancellationToken)
    {
        var bounds = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaximumUid = group.Max(message => message.Uid),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var maximumSequenceNumber = bounds?.Count ?? 0;
        var maximumUid = bounds?.MaximumUid ?? 0;

        if (!TryTokenizeSearchCriteria(criteria, out var tokens))
        {
            return new SearchExecutionResult(
                [],
                "BAD Invalid search criteria");
        }

        var parser = new SearchParser(tokens, maximumSequenceNumber, maximumUid);
        if (!parser.TryParse(out var predicate, out var failureResponse))
            return new SearchExecutionResult([], failureResponse);

        var includeBody = predicate.Requirements.HasFlag(SearchDataRequirements.Body);
        var includeRawHeaders = predicate.Requirements.HasFlag(SearchDataRequirements.RawHeaders);
        var messageQuery = BuildSearchMessageQuery(query, includeBody, includeRawHeaders);
        var matches = new List<SearchCandidate>();
        var sequenceNumber = 0;
        await foreach (var message in messageQuery
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            sequenceNumber++;
            if (predicate.IsMatch(message, sequenceNumber))
                matches.Add(new SearchCandidate(message.Id, message.Uid, sequenceNumber));
        }

        return new SearchExecutionResult(matches, null);
    }

    private static IQueryable<SearchStoredMessage> BuildSearchMessageQuery(
        IQueryable<EmailDB> query,
        bool includeBody,
        bool includeRawHeaders)
    {
        var ordered = query.OrderBy(message => message.Uid);
        if (includeBody && includeRawHeaders)
        {
            return ordered.Select(message => new SearchStoredMessage(
                message.Id,
                message.Uid,
                message.Sender,
                message.Recipient,
                message.Subject,
                message.Body,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.ModSeq,
                message.SizeBytes,
                message.RawHeaders,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.ReceivedAt));
        }

        if (includeBody)
        {
            return ordered.Select(message => new SearchStoredMessage(
                message.Id,
                message.Uid,
                message.Sender,
                message.Recipient,
                message.Subject,
                message.Body,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.ModSeq,
                message.SizeBytes,
                null,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.ReceivedAt));
        }

        if (includeRawHeaders)
        {
            return ordered.Select(message => new SearchStoredMessage(
                message.Id,
                message.Uid,
                message.Sender,
                message.Recipient,
                message.Subject,
                string.Empty,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.ModSeq,
                message.SizeBytes,
                message.RawHeaders,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.ReceivedAt));
        }

        return ordered.Select(message => new SearchStoredMessage(
            message.Id,
            message.Uid,
            message.Sender,
            message.Recipient,
            message.Subject,
            string.Empty,
            message.IsRead,
            message.IsDeleted,
            message.IsFlagged,
            message.IsDraft,
            message.IsAnswered,
            message.ModSeq,
            message.SizeBytes,
            null,
            message.MessageId,
            message.InReplyTo,
            message.Cc,
            message.ReceivedAt));
    }

    private static bool TryTokenizeSearchCriteria(
        string criteria,
        out List<SearchToken> tokens)
    {
        tokens = [];
        var index = 0;
        while (index < criteria.Length)
        {
            while (index < criteria.Length && char.IsWhiteSpace(criteria[index]))
                index++;

            if (index >= criteria.Length)
                break;

            if (tokens.Count >= MaximumSearchTokens)
                return false;

            if (criteria[index] == '(')
            {
                tokens.Add(new SearchToken(SearchTokenKind.OpenParenthesis, "("));
                index++;
                continue;
            }

            if (criteria[index] == ')')
            {
                tokens.Add(new SearchToken(SearchTokenKind.CloseParenthesis, ")"));
                index++;
                if (index < criteria.Length
                    && !char.IsWhiteSpace(criteria[index])
                    && criteria[index] != ')')
                {
                    return false;
                }
                continue;
            }

            if (criteria[index] == '"')
            {
                index++;
                var value = new StringBuilder();
                var terminated = false;
                while (index < criteria.Length)
                {
                    var character = criteria[index++];
                    if (character == '"')
                    {
                        terminated = true;
                        break;
                    }

                    if (character == '\\')
                    {
                        if (index >= criteria.Length)
                            return false;
                        character = criteria[index++];
                        if (character is not '\\' and not '"')
                            return false;
                    }

                    if (character == '\0')
                        return false;
                    value.Append(character);
                }

                if (!terminated)
                    return false;
                if (index < criteria.Length
                    && !char.IsWhiteSpace(criteria[index])
                    && criteria[index] != ')')
                {
                    return false;
                }

                tokens.Add(new SearchToken(SearchTokenKind.Atom, value.ToString()));
                continue;
            }

            var start = index;
            while (index < criteria.Length
                   && !char.IsWhiteSpace(criteria[index])
                   && criteria[index] is not '(' and not ')')
            {
                if (criteria[index] is '\r' or '\n' or '\0')
                    return false;
                index++;
            }

            if (index == start)
                return false;
            if (index < criteria.Length && criteria[index] == '(')
                return false;

            tokens.Add(new SearchToken(
                SearchTokenKind.Atom,
                criteria[start..index]));
        }

        return tokens.Count <= MaximumSearchTokens;
    }

    private static bool LooksLikeMessageSet(string value)
    {
        if (value.Length == 0 || value[0] is not ('*' or >= '0' and <= '9'))
            return false;

        foreach (var character in value)
        {
            if (character is not ('*' or ':' or ',' or >= '0' and <= '9'))
                return false;
        }

        return true;
    }

    private static bool ContainsSearchText(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MessageTextContains(SearchStoredMessage message, string value)
    {
        if (ContainsSearchText(message.Body, value)
            || HeaderBlockContains(message.RawHeaders, value))
        {
            return true;
        }

        if (message.RawHeaders is not null)
            return false;

        return ContainsSearchText(message.Sender, value)
            || ContainsSearchText(message.Recipient, value)
            || ContainsSearchText(message.Subject, value)
            || ContainsSearchText(message.Cc, value)
            || ContainsSearchText(message.MessageId, value)
            || ContainsSearchText(message.InReplyTo, value);
    }

    private static bool HeaderBlockContains(string? rawHeaders, string value)
    {
        if (string.IsNullOrEmpty(rawHeaders))
            return false;
        if (ContainsSearchText(rawHeaders, value))
            return true;

        using var reader = new StringReader(rawHeaders);
        var unfolded = new StringBuilder(rawHeaders.Length);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                unfolded.Append(' ').Append(line.Trim());
            }
            else
            {
                if (unfolded.Length > 0)
                    unfolded.Append('\n');
                unfolded.Append(line);
            }
        }

        return ContainsSearchText(unfolded.ToString(), value);
    }

    private static bool HasKeyword(SearchStoredMessage message, string keyword) =>
        keyword.ToUpperInvariant() switch
        {
            "\\SEEN" => message.IsRead,
            "\\DELETED" => message.IsDeleted,
            "\\FLAGGED" => message.IsFlagged,
            "\\DRAFT" => message.IsDraft,
            "\\ANSWERED" => message.IsAnswered,
            "\\RECENT" => false,
            _ => false,
        };

    private static bool HeaderContains(
        string? rawHeaders,
        string headerName,
        string searchValue)
    {
        if (string.IsNullOrEmpty(rawHeaders))
            return false;

        using var reader = new StringReader(rawHeaders);
        string? currentName = null;
        var currentValue = new StringBuilder();

        bool CurrentHeaderMatches() => currentName is not null
            && currentName.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            && currentValue.ToString().Contains(
                searchValue,
                StringComparison.OrdinalIgnoreCase);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                if (currentName is not null)
                    currentValue.Append(' ').Append(line.Trim());
                continue;
            }

            if (CurrentHeaderMatches())
                return true;

            currentName = null;
            currentValue.Clear();
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            currentName = line[..separator].Trim();
            currentValue.Append(line[(separator + 1)..].Trim());
        }

        return CurrentHeaderMatches();
    }

    private static bool TryGetSentDate(string? rawHeaders, out DateOnly sentDate)
    {
        sentDate = default;
        if (!TryGetHeaderValue(rawHeaders, "Date", out var dateValue)
            || !MimeKit.Utils.DateUtils.TryParse(dateValue, out var parsedDate))
        {
            return false;
        }

        sentDate = DateOnly.FromDateTime(parsedDate.Date);
        return true;
    }

    private static bool TryGetHeaderValue(
        string? rawHeaders,
        string headerName,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(rawHeaders))
            return false;

        using var reader = new StringReader(rawHeaders);
        string? currentName = null;
        string? matchedValue = null;
        var currentValue = new StringBuilder();

        bool TryUseCurrentHeader()
        {
            if (currentName is null
                || !currentName.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matchedValue = currentValue.ToString();
            return true;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                if (currentName is not null)
                    currentValue.Append(' ').Append(line.Trim());
                continue;
            }

            if (TryUseCurrentHeader())
            {
                value = matchedValue!;
                return true;
            }

            currentName = null;
            currentValue.Clear();
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            currentName = line[..separator].Trim();
            currentValue.Append(line[(separator + 1)..].Trim());
        }

        if (!TryUseCurrentHeader())
            return false;

        value = matchedValue!;
        return true;
    }

    private static bool TryParseImapDate(string dateStr, out DateTime result)
    {
        var unquoted = dateStr.Trim('"');
        return DateTime.TryParseExact(unquoted,
            ["d-MMM-yyyy", "dd-MMM-yyyy"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out result);
    }

    // — ESEARCH helpers (RFC 4731) —

    private static (string[]? returnOpts, string searchCriteria) ParseEsearchReturn(string args)
    {
        var trimmed = args.TrimStart();
        if (trimmed.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase))
        {
            var openParen = trimmed.IndexOf('(');
            var closeParen = trimmed.IndexOf(')');
            if (openParen >= 0 && closeParen > openParen)
            {
                var opts = trimmed[(openParen + 1)..closeParen]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var rest = trimmed[(closeParen + 1)..].TrimStart();
                return (opts.Length > 0 ? opts : ["ALL"], rest);
            }
        }
        return (null, args);
    }

    private static string BuildEsearchResult(string[] returnOpts, List<int> numbers)
    {
        var parts = new List<string>();
        var opts = new HashSet<string>(returnOpts.Select(o => o.ToUpperInvariant()));

        // If RETURN () with no opts, default to ALL
        if (opts.Count == 0)
            opts.Add("ALL");

        if (opts.Contains("MIN") && numbers.Count > 0)
            parts.Add($"MIN {numbers[0]}");
        if (opts.Contains("MAX") && numbers.Count > 0)
            parts.Add($"MAX {numbers[^1]}");
        if (opts.Contains("COUNT"))
            parts.Add($"COUNT {numbers.Count}");
        if (opts.Contains("ALL") && numbers.Count > 0)
            parts.Add($"ALL {FormatUidRange(numbers)}");

        return string.Join(' ', parts);
    }

    private async Task HandleSortAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        if (!_searchCommandLimiter.Wait(0))
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent search commands");
            return;
        }

        try
        {
            await HandleSortCoreAsync(writer, tag, args, session, useUid, ct);
        }
        finally
        {
            _searchCommandLimiter.Release();
        }
    }

    private async Task HandleSortCoreAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        var openParen = args.IndexOf('(');
        var closeParen = args.IndexOf(')');
        if (openParen < 0 || closeParen < 0 || closeParen <= openParen)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var sortCriteria = args[(openParen + 1)..closeParen]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(criterion => criterion.ToUpperInvariant())
            .ToArray();
        var rest = args[(closeParen + 1)..].Trim();

        var charsetSpaceIdx = rest.IndexOf(' ');
        if (charsetSpaceIdx <= 0 || string.IsNullOrWhiteSpace(rest[(charsetSpaceIdx + 1)..]))
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var searchCriteria = $"CHARSET {rest[..charsetSpaceIdx]} {rest[(charsetSpaceIdx + 1)..].Trim()}";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;

        var folderQuery = db.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == session.SelectedFolderId!.Value);
        var searchResult = await FindSearchCandidatesAsync(folderQuery, searchCriteria, ct);
        if (searchResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}");
            return;
        }

        var matchedIds = searchResult.Matches.Select(candidate => candidate.Id).ToArray();
        var query = folderQuery.Where(email => matchedIds.Contains(email.Id));
        query = ApplySortCriteria(query, sortCriteria);

        if (useUid)
        {
            var uids = await query.Select(e => e.Uid).ToListAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            var result = string.Join(' ', uids);
            await writer.WriteLineAsync($"* SORT {result}");
            await writer.WriteLineAsync($"{tag} OK UID SORT completed");
        }
        else
        {
            var sortedIds = await query.Select(e => e.Id).ToListAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            var sequenceById = searchResult.Matches.ToDictionary(
                candidate => candidate.Id,
                candidate => candidate.SequenceNumber);
            var seqNums = sortedIds.Select(id => sequenceById[id]).ToList();

            var result = string.Join(' ', seqNums);
            await writer.WriteLineAsync($"* SORT {result}");
            await writer.WriteLineAsync($"{tag} OK SORT completed");
        }
    }

    private async Task HandleThreadAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        if (!_searchCommandLimiter.Wait(0))
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent search commands");
            return;
        }

        try
        {
            await HandleThreadCoreAsync(writer, tag, args, session, useUid, ct);
        }
        finally
        {
            _searchCommandLimiter.Release();
        }
    }

    private async Task HandleThreadCoreAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var algorithm = args[..spaceIdx].ToUpperInvariant();
        var rest = args[(spaceIdx + 1)..].Trim();

        var charsetSpaceIdx = rest.IndexOf(' ');
        if (charsetSpaceIdx <= 0 || string.IsNullOrWhiteSpace(rest[(charsetSpaceIdx + 1)..]))
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var searchCriteria = $"CHARSET {rest[..charsetSpaceIdx]} {rest[(charsetSpaceIdx + 1)..].Trim()}";

        if (algorithm is not "REFERENCES" and not "ORDEREDSUBJECT")
        {
            await writer.WriteLineAsync($"{tag} BAD Unknown threading algorithm");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;

        var folderQuery = db.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == session.SelectedFolderId!.Value);
        var searchResult = await FindSearchCandidatesAsync(folderQuery, searchCriteria, ct);
        if (searchResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}");
            return;
        }

        var matchedIds = searchResult.Matches.Select(candidate => candidate.Id).ToArray();
        var identifierById = searchResult.Matches.ToDictionary(
            candidate => candidate.Id,
            candidate => useUid ? candidate.Uid : candidate.SequenceNumber);

        var emails = await folderQuery
            .Where(email => matchedIds.Contains(email.Id))
            .OrderBy(e => e.ReceivedAt)
            .Select(e => new { e.Id, e.Uid, e.MessageId, e.InReplyTo, e.Subject })
            .ToListAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        if (algorithm == "REFERENCES")
        {
            var messageIdToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < emails.Count; i++)
            {
                if (!string.IsNullOrEmpty(emails[i].MessageId))
                    messageIdToIndex.TryAdd(emails[i].MessageId!, i);
            }

            var parentOf = new int[emails.Count];
            for (var i = 0; i < parentOf.Length; i++) parentOf[i] = -1;

            for (var i = 0; i < emails.Count; i++)
            {
                if (!string.IsNullOrEmpty(emails[i].InReplyTo) &&
                    messageIdToIndex.TryGetValue(emails[i].InReplyTo!, out var parentIdx))
                {
                    parentOf[i] = parentIdx;
                }
            }

            var roots = new List<int>();
            for (var i = 0; i < parentOf.Length; i++)
            {
                if (parentOf[i] < 0) roots.Add(i);
            }

            var sb = new StringBuilder();
            foreach (var root in roots)
            {
                BuildThreadTree(sb, root, parentOf, emails.Count,
                    i => identifierById[emails[i].Id].ToString());
            }

            var cmdPrefix = useUid ? "UID THREAD" : "THREAD";
            await writer.WriteLineAsync($"* THREAD {sb}");
            await writer.WriteLineAsync($"{tag} OK {cmdPrefix} completed");
        }
        else
        {
            var groups = emails
                .GroupBy(e => NormalizeSubject(e.Subject))
                .OrderBy(g => g.Min(e => e.Uid));

            var sb = new StringBuilder();
            foreach (var group in groups)
            {
                var members = group.OrderBy(e => e.Uid).ToList();
                if (members.Count == 1)
                {
                    var id = identifierById[members[0].Id];
                    sb.Append($"({id})");
                }
                else
                {
                    sb.Append('(');
                    for (var j = 0; j < members.Count; j++)
                    {
                        var id = identifierById[members[j].Id];
                        if (j > 0) sb.Append(' ');
                        sb.Append(id);
                    }
                    sb.Append(')');
                }
            }

            var cmdPrefix = useUid ? "UID THREAD" : "THREAD";
            await writer.WriteLineAsync($"* THREAD {sb}");
            await writer.WriteLineAsync($"{tag} OK {cmdPrefix} completed");
        }
    }

    private static void BuildThreadTree(
        StringBuilder sb, int nodeIdx, int[] parentOf, int count,
        Func<int, string> idFunc)
    {
        var children = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (parentOf[i] == nodeIdx)
                children.Add(i);
        }

        if (children.Count == 0)
        {
            sb.Append($"({idFunc(nodeIdx)})");
        }
        else
        {
            sb.Append($"({idFunc(nodeIdx)}");
            foreach (var child in children)
            {
                sb.Append(' ');
                BuildThreadTree(sb, child, parentOf, count, idFunc);
            }
            sb.Append(')');
        }
    }

    private static string NormalizeSubject(string subject)
    {
        var s = subject.Trim();
        while (s.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
        {
            var colonIdx = s.IndexOf(':');
            s = s[(colonIdx + 1)..].TrimStart();
        }
        return s.ToUpperInvariant();
    }

    private static IQueryable<EmailDB> ApplySortCriteria(IQueryable<EmailDB> query, string[] criteria)
    {
        IOrderedQueryable<EmailDB>? ordered = null;

        foreach (var criterion in criteria)
        {
            var reverse = false;
            var field = criterion;

            if (field == "REVERSE" || field == "(REVERSE")
            {
                // REVERSE applies to the next criterion — handled by checking context
                continue;
            }

            var idx = Array.IndexOf(criteria, criterion);
            if (idx > 0 && (criteria[idx - 1] == "REVERSE" || criteria[idx - 1] == "(REVERSE"))
                reverse = true;

            ordered = (field, reverse) switch
            {
                ("DATE" or "ARRIVAL", false) => ordered is null
                    ? query.OrderBy(e => e.ReceivedAt)
                    : ordered.ThenBy(e => e.ReceivedAt),
                ("DATE" or "ARRIVAL", true) => ordered is null
                    ? query.OrderByDescending(e => e.ReceivedAt)
                    : ordered.ThenByDescending(e => e.ReceivedAt),
                ("SUBJECT", false) => ordered is null
                    ? query.OrderBy(e => e.Subject)
                    : ordered.ThenBy(e => e.Subject),
                ("SUBJECT", true) => ordered is null
                    ? query.OrderByDescending(e => e.Subject)
                    : ordered.ThenByDescending(e => e.Subject),
                ("FROM", false) => ordered is null
                    ? query.OrderBy(e => e.Sender)
                    : ordered.ThenBy(e => e.Sender),
                ("FROM", true) => ordered is null
                    ? query.OrderByDescending(e => e.Sender)
                    : ordered.ThenByDescending(e => e.Sender),
                ("TO", false) => ordered is null
                    ? query.OrderBy(e => e.Recipient)
                    : ordered.ThenBy(e => e.Recipient),
                ("TO", true) => ordered is null
                    ? query.OrderByDescending(e => e.Recipient)
                    : ordered.ThenByDescending(e => e.Recipient),
                ("SIZE", false) => ordered is null
                    ? query.OrderBy(e => e.SizeBytes)
                    : ordered.ThenBy(e => e.SizeBytes),
                ("SIZE", true) => ordered is null
                    ? query.OrderByDescending(e => e.SizeBytes)
                    : ordered.ThenByDescending(e => e.SizeBytes),
                _ => ordered,
            };
        }

        return ordered ?? query.OrderBy(e => e.ReceivedAt);
    }

    private async Task HandleUidExpungeAsync(
        StreamWriter writer, string tag, string uidSetArg, ImapSession session, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var emails = await GetEmailMetadataInFolderAsync(
            db,
            session.SelectedFolderId!.Value,
            ct);

        var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
        var maxUid = emails.Count > 0 ? emails[^1].Uid : 0;
        if (!TryParseMessageSet(uidSetArg, maxUid, out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var expunged = 0;
        var vanishedUids = new List<int>();

        for (var i = 0; i < emails.Count; i++)
        {
            var email = emails[i];
            if (!IsMarkedDeleted(email)) continue;
            if (!MessageSetContains(parsedMessageSet, email.Uid)) continue;

            var expungeModSeq = ++folder!.HighestModSeq;

            if (session.QresyncEnabled)
            {
                vanishedUids.Add(email.Uid);
            }
            else
            {
                var seqNum = i + 1 - expunged;
                await writer.WriteLineAsync($"* {seqNum} EXPUNGE");
            }

            db.ExpungedUids.Add(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = email.Uid,
                ModSeq = expungeModSeq,
                FolderId = session.SelectedFolderId!.Value,
            });

            AttachDelete(db, email);
            expunged++;
        }

        if (session.QresyncEnabled && vanishedUids.Count > 0)
        {
            var vanishedSet = FormatUidRange(vanishedUids);
            await writer.WriteLineAsync($"* VANISHED {vanishedSet}");
        }

        await db.SaveChangesAsync(ct);
        await writer.WriteLineAsync($"{tag} OK UID EXPUNGE completed");
    }

    private async Task HandleMoveAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleMoveCoreAsync(writer, tag, args, session, useUid: false, ct);
    }

    private async Task HandleUidMoveAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleMoveCoreAsync(writer, tag, args, session, useUid: true, ct);
    }

    private async Task HandleMoveCoreAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var messageSet = args[..spaceIdx];
        var destMailbox = UnquoteArg(args[(spaceIdx + 1)..].Trim());

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        var sourceFolder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
        if (sourceFolder is null)
        {
            await writer.WriteLineAsync($"{tag} NO Selected mailbox not found");
            return;
        }

        var destFolder = await ResolveFolderAsync(db, session.UserId, destMailbox, ct);
        if (destFolder is null)
        {
            await writer.WriteLineAsync($"{tag} NO [TRYCREATE] Destination mailbox not found");
            return;
        }

        var emails = await GetEmailMetadataInFolderAsync(db, session.SelectedFolderId!.Value, ct);
        var maximumIdentifier = useUid
            ? (emails.Count > 0 ? emails[^1].Uid : 0)
            : emails.Count;
        if (!TryParseMessageSet(messageSet, maximumIdentifier, out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var selected = emails
            .Select((email, index) => (Email: email, SequenceNumber: index + 1))
            .Where(item => MessageSetContains(
                parsedMessageSet,
                useUid ? item.Email.Uid : item.SequenceNumber))
            .ToList();

        var srcUids = new List<int>(selected.Count);
        var dstUids = new List<int>(selected.Count);
        var expungeSequenceNumbers = new List<int>(selected.Count);
        var expunged = 0;
        foreach (var item in selected)
        {
            var email = item.Email;
            var sourceUid = email.Uid;
            var sourceModSeq = ++sourceFolder.HighestModSeq;
            db.ExpungedUids.Add(new ExpungedUidDB
            {
                Id = Guid.CreateVersion7(),
                Uid = sourceUid,
                ModSeq = sourceModSeq,
                FolderId = sourceFolder.Id,
            });

            srcUids.Add(sourceUid);
            var destinationUid = destFolder.NextUid++;
            var destinationModSeq = ++destFolder.HighestModSeq;
            AttachMoveUpdate(db, email, destFolder.Id, destinationUid, destinationModSeq);
            dstUids.Add(destinationUid);

            expungeSequenceNumbers.Add(item.SequenceNumber - expunged);
            expunged++;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        if (session.QresyncEnabled && srcUids.Count > 0)
        {
            await writer.WriteLineAsync($"* VANISHED {FormatUidRange(srcUids)}");
        }
        else
        {
            foreach (var sequenceNumber in expungeSequenceNumbers)
                await writer.WriteLineAsync($"* {sequenceNumber} EXPUNGE");
        }

        var commandName = useUid ? "UID MOVE" : "MOVE";
        await writer.WriteLineAsync(
            $"{tag} OK [COPYUID {destFolder.UidValidity} {FormatUidSet(srcUids)} {FormatUidSet(dstUids)}] {commandName} completed");
    }

    private async Task HandleAppendAsync(
        BoundedLineReader reader,
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        int maximumMessageSize,
        CancellationTokenSource timeout,
        int connectionTimeoutSeconds)
    {
        if (!_messageWriteCommandLimiter.Wait(0))
        {
            if (NonSynchronizingLiteralRegex().IsMatch(args))
            {
                await writer.WriteLineAsync("* BYE Too many concurrent APPEND commands");
                session.State = ImapState.Logout;
            }
            else
            {
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent APPEND commands");
            }

            return;
        }

        try
        {
            await HandleAppendCoreAsync(
                reader,
                writer,
                tag,
                args,
                session,
                maximumMessageSize,
                timeout,
                connectionTimeoutSeconds);
        }
        finally
        {
            _messageWriteCommandLimiter.Release();
        }
    }

    private async Task HandleAppendCoreAsync(
        BoundedLineReader reader,
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        int maximumMessageSize,
        CancellationTokenSource timeout,
        int connectionTimeoutSeconds)
    {
        var ct = timeout.Token;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var remaining = args;
        var pendingMessages = new List<EmailDB>();
        string? targetMailbox = null;
        long pendingBytes = 0;

        while (true)
        {
            var (mailboxName, flags, internalDate, literalSize) = ParseAppendArgs(remaining);

            if (mailboxName is null || literalSize is null)
            {
                if (pendingMessages.Count == 0)
                {
                    await writer.WriteLineAsync($"{tag} BAD Syntax error");
                    return;
                }
                break;
            }

            if (targetMailbox is null)
            {
                var targetFolder = await ResolveFolderAsync(db, session.UserId, mailboxName, ct);
                if (targetFolder is null)
                {
                    await writer.WriteLineAsync($"{tag} NO [TRYCREATE] Mailbox not found");
                    return;
                }

                targetMailbox = mailboxName;
            }

            var isLiteralPlus = remaining.Contains("{" + literalSize + "+}");
            if (pendingMessages.Count >= MaximumMultiAppendMessages)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "[LIMIT] APPEND contains too many messages");
                return;
            }

            if (literalSize < 0
                || literalSize > maximumMessageSize
                || literalSize > maximumMessageSize - pendingBytes)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "[TOOBIG] APPEND exceeds the command size limit");
                return;
            }

            var nextPendingBytes = pendingBytes + literalSize.Value;
            if (!await HasUserQuotaCapacityAsync(db, session.UserId, nextPendingBytes, ct))
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "[OVERQUOTA] APPEND exceeds the mailbox quota");
                return;
            }

            if (!isLiteralPlus)
                await writer.WriteLineAsync("+ Ready for literal data");

            var buffer = new char[literalSize.Value];
            var totalRead = 0;
            while (totalRead < literalSize.Value)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                var read = await reader.ReadAsync(buffer.AsMemory(totalRead, literalSize.Value - totalRead), ct);
                if (read == 0)
                    throw new EndOfStreamException("The APPEND literal ended before its declared size.");
                totalRead += read;
            }

            var messageData = new string(buffer, 0, totalRead);
            if (messageData.Contains('\0'))
            {
                await writer.WriteLineAsync($"{tag} NO APPEND content contains a NUL byte");
                return;
            }

            var (subject, body, headers) = MailMessageParser.Parse(messageData);
            var sender = MailMessageParser.ExtractHeaderValue(headers, "From");
            var recipient = MailMessageParser.ExtractHeaderValue(headers, "To");

            var msgId = MailMessageParser.ExtractHeaderValue(headers, "Message-ID");
            var inReplyTo = MailMessageParser.ExtractHeaderValue(headers, "In-Reply-To");

            var email = new EmailDB
            {
                Id = Guid.CreateVersion7(),
                Sender = sender,
                Recipient = recipient,
                Subject = subject.Length > 998 ? subject[..998] : subject,
                Body = body,
                RawHeaders = headers,
                SizeBytes = MailWireEncoding.Instance.GetByteCount(messageData),
                MessageId = msgId,
                InReplyTo = inReplyTo,
                Cc = MailMessageParser.ExtractHeaderValue(headers, "Cc"),
                EmailObjectId = Guid.CreateVersion7().ToString("N"),
                ThreadObjectId = GenerateThreadObjectId(inReplyTo, msgId),
                ReceivedAt = internalDate ?? DateTime.UtcNow,
            };

            foreach (var flag in flags)
            {
                switch (flag.ToUpperInvariant())
                {
                    case "\\SEEN": email.IsRead = true; break;
                    case "\\DELETED": email.IsDeleted = true; break;
                    case "\\FLAGGED": email.IsFlagged = true; break;
                    case "\\DRAFT": email.IsDraft = true; break;
                    case "\\ANSWERED": email.IsAnswered = true; break;
                }
            }

            pendingMessages.Add(email);
            pendingBytes = nextPendingBytes;

            timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
            var nextLineResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct);
            if (nextLineResult.IsTooLong)
            {
                await writer.WriteLineAsync("* BYE APPEND continuation is too long");
                session.State = ImapState.Logout;
                return;
            }
            var nextLine = nextLineResult.Value;
            if (nextLine is null || nextLine.Length == 0 || !nextLine.TrimStart().StartsWith('(') && !nextLine.TrimStart().StartsWith('{'))
                break;

            remaining = $"\"{EscapeImapString(targetMailbox)}\" {nextLine.Trim()}";
        }

        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        var folder = await ResolveFolderAsync(db, session.UserId, targetMailbox!, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO [TRYCREATE] Mailbox not found");
            return;
        }

        if (!await HasUserQuotaCapacityAsync(db, session.UserId, pendingBytes, ct))
        {
            await writer.WriteLineAsync($"{tag} NO [OVERQUOTA] APPEND exceeds the mailbox quota");
            return;
        }

        var allUids = new List<int>(pendingMessages.Count);
        foreach (var email in pendingMessages)
        {
            email.Uid = folder.NextUid++;
            email.ModSeq = ++folder.HighestModSeq;
            email.FolderId = folder.Id;
            allUids.Add(email.Uid);
        }

        db.Emails.AddRange(pendingMessages);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var uidSetStr = FormatUidRange(allUids);
        await writer.WriteLineAsync($"{tag} OK [APPENDUID {folder.UidValidity} {uidSetStr}] APPEND completed");
    }

    private static async Task RejectAppendBeforeLiteralAsync(
        StreamWriter writer,
        string tag,
        ImapSession session,
        bool isLiteralPlus,
        string response)
    {
        if (isLiteralPlus)
        {
            await writer.WriteLineAsync($"* BYE {response}");
            session.State = ImapState.Logout;
            return;
        }

        await writer.WriteLineAsync($"{tag} NO {response}");
    }

    private static async Task<bool> HasUserQuotaCapacityAsync(
        EmailDbContext db,
        Guid userId,
        long addedBytes,
        CancellationToken ct)
    {
        var quotaBytes = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (long?)user.QuotaBytes)
            .SingleOrDefaultAsync(ct);
        if (quotaBytes is null)
            return false;
        if (quotaBytes <= 0)
            return true;

        var usedBytes = await db.Emails
            .AsNoTracking()
            .Where(email => email.Folder.Inbox.OwnerId == userId)
            .SumAsync(email => (long?)email.SizeBytes, ct)
            ?? 0;
        return usedBytes < quotaBytes
            && addedBytes <= quotaBytes - usedBytes;
    }

    [GeneratedRegex(@"\{\d+\+\}\s*$")]
    private static partial Regex NonSynchronizingLiteralRegex();

    [GeneratedRegex(@"\{([0-9]+)(\+)?\}$")]
    private static partial Regex CommandLiteralRegex();

    private static (string? mailboxName, List<string> flags, DateTime? internalDate, int? literalSize) ParseAppendArgs(string args)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 1)
            return (null, [], null, null);

        var mailboxName = UnquoteArg(tokens[0]);
        var flags = new List<string>();
        DateTime? internalDate = null;
        int? literalSize = null;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.StartsWith('('))
            {
                var flagStr = token.TrimStart('(').TrimEnd(')');
                if (!token.Contains(')'))
                {
                    while (i + 1 < tokens.Count)
                    {
                        i++;
                        flagStr += " " + tokens[i].TrimEnd(')');
                        if (tokens[i].Contains(')')) break;
                    }
                }
                flags.AddRange(flagStr.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            else if (token.StartsWith('{') && token.EndsWith('}'))
            {
                if (int.TryParse(token[1..^1].TrimEnd('+'), out var size))
                    literalSize = size;
            }
            else if (token.StartsWith('"') || char.IsDigit(token[0]))
            {
                if (DateTime.TryParse(UnquoteArg(token), out var dt))
                    internalDate = dt;
            }
        }

        if (literalSize is null)
        {
            var braceIdx = args.LastIndexOf('{');
            var braceEnd = args.LastIndexOf('}');
            if (braceIdx >= 0 && braceEnd > braceIdx)
            {
                if (int.TryParse(args[(braceIdx + 1)..braceEnd].TrimEnd('+'), out var size))
                    literalSize = size;
            }
        }

        return (mailboxName, flags, internalDate, literalSize);
    }

    private async Task HandleIdleAsync(
        BoundedLineReader reader, StreamWriter writer, string tag,
        ImapSession session, CancellationTokenSource timeout, int connectionTimeoutSeconds)
    {
        await writer.WriteLineAsync("+ idling");

        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        var lastKnownCount = 0;
        long lastKnownModSeq = 0;

        if (session.SelectedFolderId is not null)
        {
            using var initScope = scopeFactory.CreateScope();
            var initDb = initScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            lastKnownCount = await initDb.Emails.CountAsync(e => e.FolderId == session.SelectedFolderId.Value, timeout.Token);
            var folder = await initDb.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == session.SelectedFolderId.Value, timeout.Token);
            lastKnownModSeq = folder?.HighestModSeq ?? 0;
        }

        var readTask = reader.ReadLineAsync(MaximumCommandLineCharacters, timeout.Token).AsTask();
        while (!timeout.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5), timeout.Token));

            if (completed == readTask)
            {
                var lineResult = await readTask;
                if (lineResult.IsTooLong)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                    await writer.WriteLineAsync($"{tag} BAD IDLE terminator is too long");
                    return;
                }
                var line = lineResult.Value;
                if (line is null)
                    break;

                if (line.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                    await writer.WriteLineAsync($"{tag} OK IDLE terminated");
                    return;
                }

                timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                await writer.WriteLineAsync($"{tag} BAD IDLE requires DONE");
                return;
            }
            else if (session.SelectedFolderId is not null)
            {
                // DB polling: check for new messages or flag changes
                try
                {
                    using var pollScope = scopeFactory.CreateScope();
                    var pollDb = pollScope.ServiceProvider.GetRequiredService<EmailDbContext>();

                    var currentCount = await pollDb.Emails.CountAsync(
                        e => e.FolderId == session.SelectedFolderId.Value, timeout.Token);
                    var folder = await pollDb.Folders.AsNoTracking()
                        .FirstOrDefaultAsync(f => f.Id == session.SelectedFolderId.Value, timeout.Token);
                    var currentModSeq = folder?.HighestModSeq ?? 0;

                    if (currentCount != lastKnownCount)
                    {
                        await writer.WriteLineAsync($"* {currentCount} EXISTS");
                        lastKnownCount = currentCount;
                    }

                    if (currentModSeq > lastKnownModSeq)
                    {
                        // Notify about changed flags since last check
                        var changed = await SelectEmailMetadata(
                                pollDb.Emails.Where(e => e.FolderId == session.SelectedFolderId.Value
                                    && e.ModSeq > lastKnownModSeq))
                            .OrderBy(e => e.Uid)
                            .ToListAsync(timeout.Token);

                        if (changed.Count > 0)
                        {
                            var allIds = await pollDb.Emails
                                .Where(e => e.FolderId == session.SelectedFolderId.Value)
                                .OrderBy(e => e.Uid)
                                .Select(e => e.Id)
                                .ToListAsync(timeout.Token);

                            foreach (var email in changed)
                            {
                                var seqIdx = allIds.IndexOf(email.Id);
                                if (seqIdx >= 0)
                                {
                                    var seqNum = seqIdx + 1;
                                    var flags = BuildFlagsList(email);
                                    await writer.WriteLineAsync($"* {seqNum} FETCH (FLAGS ({flags}))");
                                }
                            }
                        }

                        lastKnownModSeq = currentModSeq;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A duplex stream that reads from one underlying stream and writes to another.
    /// Used for COMPRESS=DEFLATE where inflate and deflate are separate streams over the same transport.
    /// </summary>
    private sealed class CompressedDuplexStream(Stream readStream, Stream writeStream) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => readStream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            readStream.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            readStream.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => writeStream.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            writeStream.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            writeStream.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => writeStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => writeStream.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                readStream.Dispose();
                writeStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
