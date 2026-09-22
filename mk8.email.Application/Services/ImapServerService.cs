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
using MimeKit;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Configuration;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Data;
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
    private const int MaximumKeywordsPerMessage = 128;
    private const int MaximumKeywordLength = 255;
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
        public bool Utf8Enabled { get; set; }
        public bool CompressEnabled { get; set; }
        public HashSet<int> SavedSearchUids { get; set; } = [];
        public required string RemoteIp { get; init; }
        public int AuthenticationFailures { get; set; }
    }

    private enum ImapState { NotAuthenticated, Authenticated, Selected, Logout }

    private enum SessionUpgrade { None, StartTls, Compress }

    private enum BinaryFetchKind { Content, Size }

    private readonly record struct MessageSetRange(int Start, int End);
    private readonly record struct BinaryFetchRequest(
        BinaryFetchKind Kind,
        string Section,
        bool Peek,
        uint? Offset,
        uint? Count);
    private readonly record struct MailboxLocation(Guid InboxId, string FolderName);
    private sealed record MailboxFolderInfo(
        string InboxName,
        string Domain,
        string FolderName,
        bool IsPrimary,
        bool IsSubscribed);
    private sealed record MailboxListEntry(
        string FullName,
        string? FolderName,
        bool IsSelectable,
        bool HasChildren,
        bool IsSubscribed);
    private sealed record ListCommandOptions(
        string Reference,
        IReadOnlyList<string> Patterns,
        bool IsExtended,
        bool SelectSubscribed,
        bool SelectRemote,
        bool SelectRecursiveMatch,
        bool SelectSpecialUse,
        bool ReturnSubscribed,
        bool ReturnChildren,
        bool ReturnSpecialUse,
        string[] StatusItems);

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
                        await writer.WriteLineAsync($"{tag} BAD COMPRESS already active");
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

    private async Task HandleCapabilityAsync(
        StreamWriter writer,
        string tag,
        GlobalConfigDB config,
        ImapSession session)
    {
        var caps =
            "IMAP4rev1 LITERAL+ IDLE NAMESPACE SPECIAL-USE UIDPLUS LIST-EXTENDED LIST-STATUS " +
            "ID ENABLE MOVE UNSELECT QUOTA CONDSTORE QRESYNC ESEARCH SEARCHRES UTF8=ACCEPT " +
            $"SORT THREAD=ORDEREDSUBJECT THREAD=REFERENCES BINARY OBJECTID MULTIAPPEND STATUS=SIZE COMPRESS=DEFLATE " +
            $"APPENDLIMIT={config.MaxMessageSizeBytes}";
        if (session.IsSecure)
        {
            caps += " AUTH=PLAIN SASL-IR";
            if (env.OAuth.EnableOAuth)
                caps += " AUTH=XOAUTH2";
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

        if (mechanism is not ("PLAIN" or "XOAUTH2")
            || mechanism == "XOAUTH2" && !env.OAuth.EnableOAuth)
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

        if (mechanism == "XOAUTH2")
        {
            if (!OAuthSasl.TryParseXOAuth2(encoded, out var oauthUsername, out var accessToken))
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync($"{tag} NO Authentication failed");
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<IOAuthTokenService>();
            var oauthUser = await tokenService.AuthenticateAccessTokenAsync(
                accessToken,
                "imap",
                ct);
            if (oauthUser is null
                || !string.Equals(
                    oauthUsername,
                    oauthUser.Username,
                    StringComparison.OrdinalIgnoreCase))
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync($"{tag} NO Authentication failed");
                return;
            }

            session.UserId = oauthUser.Id;
            session.UserName = oauthUser.Username;
            session.State = ImapState.Authenticated;
            await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed");
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
        if (!TryParseListCommand(args, session.Utf8Enabled, out var options, out var failureResponse))
        {
            await writer.WriteLineAsync($"{tag} BAD {failureResponse}");
            return;
        }

        if (!options.IsExtended
            && options.Patterns.Count == 1
            && options.Patterns[0] == string.Empty)
        {
            await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
            await writer.WriteLineAsync($"{tag} OK LIST completed");
            return;
        }

        var folders = await GetUserFoldersAsync(session.UserId, ct);
        var entries = BuildMailboxListEntries(folders);

        using var scope = options.StatusItems.Length > 0 ? scopeFactory.CreateScope() : null;
        var db = scope?.ServiceProvider.GetRequiredService<EmailDbContext>();

        foreach (var entry in entries)
        {
            if (!MatchesAnyPattern(entry.FullName, options.Reference, options.Patterns))
                continue;

            var matchesSelection = MatchesListSelection(entry, options);
            var includeChildInfo = false;
            if (!matchesSelection && options.SelectRecursiveMatch)
            {
                var descendantPrefix = entry.FullName + "/";
                includeChildInfo = entries.Any(descendant =>
                    descendant.FullName.StartsWith(descendantPrefix, StringComparison.Ordinal)
                    && MatchesListSelection(descendant, options)
                    && !MatchesAnyPattern(descendant.FullName, options.Reference, options.Patterns));
            }

            if (!matchesSelection && !includeChildInfo)
                continue;

            var attrs = GetFolderAttributes(
                entry.FolderName,
                entry.IsSelectable,
                entry.HasChildren,
                includeSubscribed: (options.SelectSubscribed || options.ReturnSubscribed)
                    && entry.IsSubscribed,
                useNonExistent: options.IsExtended);
            var childInfo = includeChildInfo
                ? $" (CHILDINFO ({BuildChildInfoCriteria(options)}))"
                : string.Empty;
            await writer.WriteLineAsync(
                $"* LIST ({attrs}) \"/\" \"{EscapeImapString(FormatWireMailboxName(entry.FullName, session.Utf8Enabled))}\"{childInfo}");

            if (entry.IsSelectable
                && matchesSelection
                && db is not null
                && options.StatusItems.Length > 0)
            {
                var folder = await ResolveFolderAsync(db, session.UserId, entry.FullName, ct);
                if (folder is not null)
                {
                    var statusResult = await BuildStatusResultAsync(db, folder, options.StatusItems, ct);
                    await writer.WriteLineAsync(
                        $"* STATUS \"{EscapeImapString(FormatWireMailboxName(entry.FullName, session.Utf8Enabled))}\" ({statusResult})");
                }
            }
        }

        await writer.WriteLineAsync($"{tag} OK LIST completed");
    }

    private async Task HandleLsubAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!TryParseMailboxArgs(args, session.Utf8Enabled, out var reference, out var pattern))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }

        var folders = await GetUserFoldersAsync(session.UserId, ct, subscribedOnly: true);

        foreach (var entry in BuildMailboxListEntries(folders))
        {
            if (MatchesPattern(entry.FullName, reference, pattern))
            {
                var attrs = GetFolderAttributes(
                    entry.FolderName,
                    entry.IsSelectable,
                    entry.HasChildren);
                await writer.WriteLineAsync(
                    $"* LSUB ({attrs}) \"/\" \"{EscapeImapString(FormatWireMailboxName(entry.FullName, session.Utf8Enabled))}\"");
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

        if (!TryParseMailboxName(selectArgs, session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }

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
        var keywordSets = await db.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == folder.Id)
            .Select(email => email.Keywords)
            .ToListAsync(ct);
        var keywords = keywordSets
            .SelectMany(keywordSet => keywordSet ?? [])
            .Where(IsValidImapKeyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        session.SelectedFolderId = folder.Id;
        session.SelectedFolderName = mailboxName;
        session.SelectedReadOnly = readOnly;
        session.SavedSearchUids = [];
        session.State = ImapState.Selected;

        await writer.WriteLineAsync($"* {totalCount} EXISTS");
        await writer.WriteLineAsync("* 0 RECENT");
        var definedFlags = keywords.Length == 0
            ? "\\Seen \\Answered \\Flagged \\Deleted \\Draft"
            : $"\\Seen \\Answered \\Flagged \\Deleted \\Draft {string.Join(' ', keywords)}";
        await writer.WriteLineAsync($"* FLAGS ({definedFlags})");
        await writer.WriteLineAsync("* OK [PERMANENTFLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft \\*)] Flags permitted");
        await writer.WriteLineAsync($"* OK [UIDVALIDITY {folder.UidValidity}]");
        await writer.WriteLineAsync($"* OK [UIDNEXT {folder.NextUid}]");
        await writer.WriteLineAsync($"* OK [HIGHESTMODSEQ {folder.HighestModSeq}]");
        await writer.WriteLineAsync(
            $"* OK [MAILBOXID ({FormatMailboxObjectId(folder)})] Mailbox identifier");

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
        if (!TryParseMailboxName(args.Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var location = await ResolveMailboxLocationAsync(db, session.UserId, mailboxName, ct);
        if (location is null || !IsValidFolderName(location.Value.FolderName))
        {
            await writer.WriteLineAsync($"{tag} NO [CANNOT] Invalid mailbox name");
            return;
        }

        var exists = await db.Folders.AnyAsync(
            folder => folder.InboxId == location.Value.InboxId
                   && folder.Name == location.Value.FolderName,
            ct);
        if (exists)
        {
            await writer.WriteLineAsync($"{tag} NO [ALREADYEXISTS] Mailbox already exists");
            return;
        }

        var folder = new FolderDB
        {
            Id = Guid.CreateVersion7(),
            Name = location.Value.FolderName,
            InboxId = location.Value.InboxId,
        };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);

        await writer.WriteLineAsync(
            $"{tag} OK [MAILBOXID ({FormatMailboxObjectId(folder)})] CREATE completed");
    }

    private async Task HandleDeleteAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!TryParseMailboxName(args.Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, mailboxName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Mailbox not found");
            return;
        }

        if (IsSystemFolder(folder.Name))
        {
            await writer.WriteLineAsync($"{tag} NO [CANNOT] System mailboxes cannot be deleted");
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
        var parsedArgs = ParseTwoMailboxArgs(args, session.Utf8Enabled);
        if (parsedArgs is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        var (oldName, newName) = parsedArgs.Value;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var folder = await ResolveFolderAsync(db, session.UserId, oldName, ct);
        if (folder is null)
        {
            await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Mailbox not found");
            return;
        }

        if (IsSystemFolder(folder.Name))
        {
            await writer.WriteLineAsync($"{tag} NO [CANNOT] System mailboxes cannot be renamed");
            return;
        }

        var destination = await ResolveMailboxLocationAsync(db, session.UserId, newName, ct);
        if (destination is null
            || destination.Value.InboxId != folder.InboxId
            || !IsValidFolderName(destination.Value.FolderName))
        {
            await writer.WriteLineAsync($"{tag} NO [CANNOT] Invalid rename destination");
            return;
        }

        var oldFolderName = folder.Name;
        var affected = await db.Folders
            .Where(candidate => candidate.InboxId == folder.InboxId
                             && (candidate.Name == oldFolderName
                                 || candidate.Name.StartsWith(oldFolderName + "/")))
            .ToListAsync(ct);
        var renamed = affected.ToDictionary(
            candidate => candidate.Id,
            candidate => destination.Value.FolderName + candidate.Name[oldFolderName.Length..]);
        var renamedNames = renamed.Values.ToHashSet(StringComparer.Ordinal);
        var affectedIds = affected.Select(candidate => candidate.Id).ToHashSet();
        var existingNames = await db.Folders
            .AsNoTracking()
            .Where(candidate => candidate.InboxId == folder.InboxId
                             && !affectedIds.Contains(candidate.Id))
            .Select(candidate => candidate.Name)
            .ToListAsync(ct);
        if (existingNames.Any(renamedNames.Contains))
        {
            await writer.WriteLineAsync($"{tag} NO [ALREADYEXISTS] Rename destination already exists");
            return;
        }

        foreach (var candidate in affected)
            candidate.Name = renamed[candidate.Id];
        await db.SaveChangesAsync(ct);

        if (session.SelectedFolderName is not null
            && (string.Equals(session.SelectedFolderName, oldName, StringComparison.OrdinalIgnoreCase)
                || session.SelectedFolderName.StartsWith(oldName + "/", StringComparison.OrdinalIgnoreCase)))
        {
            session.SelectedFolderName = newName + session.SelectedFolderName[oldName.Length..];
        }

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

        if (!TryParseMailboxName(args[..parenIdx].Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }
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

        await writer.WriteLineAsync(
            $"* STATUS \"{EscapeImapString(FormatWireMailboxName(mailboxName, session.Utf8Enabled))}\" ({statusResult})");
        await writer.WriteLineAsync($"{tag} OK STATUS completed");
    }

    private static async Task<string> BuildStatusResultAsync(
        EmailDbContext db, FolderDB folder, string[] statusItems, CancellationToken ct)
    {
        int? totalCount = null;
        int? unseenCount = null;
        long? totalSize = null;

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
                case "SIZE":
                    totalSize ??= await db.Emails
                        .Where(email => email.FolderId == folder.Id)
                        .SumAsync(email => (long?)email.SizeBytes, ct)
                        ?? 0;
                    results.Append($"SIZE {totalSize}");
                    break;
                case "MAILBOXID":
                    results.Append($"MAILBOXID ({FormatMailboxObjectId(folder)})");
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
        var fetchItems = StripFetchList(args[(spaceIdx + 1)..]);
        if (!TryParseBinaryFetchRequests(fetchItems, out var binaryRequests))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid BINARY data item");
            return;
        }

        var implicitSeen = ShouldSetSeen(fetchItems, binaryRequests);

        if (fetchItems.Contains("MODSEQ", StringComparison.OrdinalIgnoreCase))
            session.CondstoreEnabled = true;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        var folderId = session.SelectedFolderId!.Value;
        var messageQuery = db.Emails.AsNoTracking().Where(email => email.FolderId == folderId);
        var orderedUids = await messageQuery
            .OrderBy(email => email.Uid)
            .Select(email => email.Uid)
            .ToListAsync(ct);
        var maximumIdentifier = useUid
            ? orderedUids.LastOrDefault()
            : orderedUids.Count;
        if (!TryResolveMessageSet(
                messageSet,
                maximumIdentifier,
                orderedUids,
                useUid,
                session.SavedSearchUids,
                out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var includeStoredContent = FetchNeedsStoredContent(fetchItems, binaryRequests);
        var normalizedFetchItems = fetchItems
            .ToUpperInvariant()
            .Replace("BODY.PEEK[", "BODY[");
        var needsMimeProjection = normalizedFetchItems.Contains("BODYSTRUCTURE", StringComparison.Ordinal)
            || BodyStandaloneRegex().IsMatch(normalizedFetchItems)
            || NumericBodySectionRegex().IsMatch(normalizedFetchItems)
            || binaryRequests.Count > 0;
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

            using var mimeMessage = needsMimeProjection
                ? ImapMimeMessage.TryParse(BuildRfc822(email))
                : null;

            if (!TryDecodeBinarySections(
                    mimeMessage,
                    binaryRequests,
                    out var binarySections,
                    out var binaryFailure))
            {
                await PersistSeenUpdatesAsync(db, seenUpdates, ct);
                await writer.WriteLineAsync($"{tag} NO {binaryFailure}");
                return;
            }

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

            var response = BuildFetchResponse(
                sequenceNumber,
                email,
                fetchItems,
                useUid,
                mimeMessage,
                binaryRequests,
                binarySections);
            await writer.WriteLineAsync(response);
        }

        await PersistSeenUpdatesAsync(db, seenUpdates, ct);

        var commandName = useUid ? "UID FETCH" : "FETCH";
        await writer.WriteLineAsync($"{tag} OK {commandName} completed");
    }

    private static async Task PersistSeenUpdatesAsync(
        EmailDbContext db,
        IReadOnlyCollection<EmailDB> seenUpdates,
        CancellationToken ct)
    {
        foreach (var update in seenUpdates)
        {
            db.Emails.Attach(update);
            db.Entry(update).Property(email => email.IsRead).IsModified = true;
            db.Entry(update).Property(email => email.ModSeq).IsModified = true;
        }

        if (seenUpdates.Count > 0)
            await db.SaveChangesAsync(ct);
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
        if (!TryResolveMessageSet(
                sequenceSet,
                emails.Count,
                emails.Select(email => email.Uid).ToList(),
                useUid: false,
                session.SavedSearchUids,
                out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var selected = emails
            .Select((email, index) => (Email: email, SequenceNumber: index + 1))
            .Where(item => MessageSetContains(parsedMessageSet, item.SequenceNumber))
            .ToList();
        var flagsList = flagsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!IsStoreAction(action))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid STORE action");
            return;
        }
        if (!TryValidateFlagList(flagsList, out var flagFailure))
        {
            await writer.WriteLineAsync($"{tag} BAD {flagFailure}");
            return;
        }

        var isSilent = action.Contains(".SILENT");
        var modified = new List<int>();
        var applicable = new List<(EmailDB Email, int SequenceNumber)>();

        foreach (var item in selected)
        {
            var email = item.Email;
            var seqNum = item.SequenceNumber;

            if (unchangedSince is not null && email.ModSeq > unchangedSince.Value)
            {
                modified.Add(seqNum);
                continue;
            }

            if (!TryApplyFlags(email, action, flagsList, out _))
            {
                await writer.WriteLineAsync($"{tag} NO [LIMIT] Too many keywords");
                return;
            }

            applicable.Add(item);
        }

        if (applicable.Count > 0)
        {
            var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
            var newModSeq = ++folder!.HighestModSeq;

            foreach (var item in applicable)
            {
                var tracked = AttachFlagUpdate(db, item.Email, newModSeq);

                if (!isSilent)
                {
                    var flags = BuildFlagsList(tracked);
                    if (session.CondstoreEnabled)
                        await writer.WriteLineAsync($"* {item.SequenceNumber} FETCH (FLAGS ({flags}) MODSEQ ({newModSeq}))");
                    else
                        await writer.WriteLineAsync($"* {item.SequenceNumber} FETCH (FLAGS ({flags}))");
                }
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
            var (returnOptions, _) = ParseEsearchReturn(args);
            if (returnOptions?.Any(option => option.Equals("SAVE", StringComparison.OrdinalIgnoreCase)) == true)
                session.SavedSearchUids = [];
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
        var normalizedReturnOptions = returnOptions?
            .Select(option => option.ToUpperInvariant())
            .ToArray();
        if (normalizedReturnOptions is not null
            && normalizedReturnOptions.Any(option => option is not ("MIN" or "MAX" or "COUNT" or "ALL" or "SAVE")))
        {
            await writer.WriteLineAsync($"{tag} BAD Unsupported SEARCH return option");
            return;
        }

        var saveResults = normalizedReturnOptions?.Contains("SAVE") == true;
        if (session.Utf8Enabled && StartsWithCharsetSearchKey(searchCriteria))
        {
            await writer.WriteLineAsync($"{tag} BAD SEARCH CHARSET is not permitted after UTF8=ACCEPT");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var query = db.Emails
            .AsNoTracking()
            .Where(email => email.FolderId == session.SelectedFolderId!.Value);
        var searchResult = await FindSearchCandidatesAsync(
            query,
            searchCriteria.Trim(),
            session.SavedSearchUids,
            session.Utf8Enabled,
            ct);
        if (searchResult.FailureResponse is not null)
        {
            if (saveResults
                && searchResult.FailureResponse.StartsWith("NO", StringComparison.OrdinalIgnoreCase))
            {
                session.SavedSearchUids = [];
            }
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}");
            return;
        }

        var numbers = searchResult.Matches
            .Select(candidate => useUid ? candidate.Uid : candidate.SequenceNumber)
            .ToList();
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        if (saveResults)
        {
            session.SavedSearchUids = SelectSavedSearchUids(
                normalizedReturnOptions!,
                searchResult.Matches);
        }

        var responseOptions = normalizedReturnOptions?
            .Where(option => option != "SAVE")
            .ToArray();
        if (responseOptions is { Length: > 0 })
        {
            var result = BuildEsearchResult(
                responseOptions,
                numbers,
                searchResult.HighestModSequence);
            var uidMarker = useUid ? " UID" : string.Empty;
            var resultSuffix = result.Length > 0 ? $" {result}" : string.Empty;
            await writer.WriteLineAsync($"* ESEARCH (TAG \"{tag}\"){uidMarker}{resultSuffix}");
        }
        else if (returnOptions is null)
        {
            var result = string.Join(' ', numbers);
            var numberSuffix = result.Length == 0 ? string.Empty : $" {result}";
            var modSequenceSuffix = searchResult.HighestModSequence is { } highestModSequence
                ? $" (MODSEQ {highestModSequence})"
                : string.Empty;
            await writer.WriteLineAsync($"* SEARCH{numberSuffix}{modSequenceSuffix}");
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
        if (!TryParseMailboxName(args[(spaceIdx + 1)..].Trim(), session.Utf8Enabled, out var destMailbox))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid destination mailbox name");
            return;
        }

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
        if (!TryResolveMessageSet(
                messageSet,
                maximumIdentifier,
                emails.Select(email => email.Uid).ToList(),
                useUid,
                session.SavedSearchUids,
                out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var selected = emails
            .Where((email, index) => MessageSetContains(
                parsedMessageSet,
                useUid ? email.Uid : index + 1))
            .ToList();

        var commandName = useUid ? "UID COPY" : "COPY";
        if (selected.Count == 0)
        {
            await writer.WriteLineAsync($"{tag} OK {commandName} completed");
            return;
        }

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
        if (!IsStoreAction(action))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid STORE action");
            return;
        }
        if (!TryValidateFlagList(flagsList, out var flagFailure))
        {
            await writer.WriteLineAsync($"{tag} BAD {flagFailure}");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EmailDbContext>();

        var emails = await GetEmailMetadataInFolderAsync(db, session.SelectedFolderId!.Value, ct);
        var maxUid = emails.Count > 0 ? emails[^1].Uid : 0;
        if (!TryResolveMessageSet(
                storeUidSet,
                maxUid,
                emails.Select(email => email.Uid).ToList(),
                useUid: true,
                session.SavedSearchUids,
                out var parsedMessageSet))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set");
            return;
        }

        var isSilent = action.Contains(".SILENT");
        var modified = new List<int>();
        var applicable = new List<(EmailDB Email, int SequenceNumber)>();

        for (var i = 0; i < emails.Count; i++)
        {
            var email = emails[i];
            if (!MessageSetContains(parsedMessageSet, email.Uid)) continue;

            if (unchangedSince is not null && email.ModSeq > unchangedSince.Value)
            {
                modified.Add(email.Uid);
                continue;
            }

            if (!TryApplyFlags(email, action, flagsList, out _))
            {
                await writer.WriteLineAsync($"{tag} NO [LIMIT] Too many keywords");
                return;
            }

            applicable.Add((email, i + 1));
        }

        if (applicable.Count > 0)
        {
            var folder = await db.Folders.FindAsync([session.SelectedFolderId!.Value], ct);
            var newModSeq = ++folder!.HighestModSeq;

            foreach (var item in applicable)
            {
                var tracked = AttachFlagUpdate(db, item.Email, newModSeq);

                if (isSilent)
                    continue;

                var flags = BuildFlagsList(tracked);
                if (session.CondstoreEnabled)
                    await writer.WriteLineAsync($"* {item.SequenceNumber} FETCH (UID {item.Email.Uid} FLAGS ({flags}) MODSEQ ({newModSeq}))");
                else
                    await writer.WriteLineAsync($"* {item.SequenceNumber} FETCH (UID {item.Email.Uid} FLAGS ({flags}))");
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
            else if (ext.Equals("UTF8=ACCEPT", StringComparison.OrdinalIgnoreCase))
            {
                session.Utf8Enabled = true;
                enabled.Add("UTF8=ACCEPT");
            }
        }

        var enabledStr = enabled.Count > 0 ? string.Join(' ', enabled) : "";
        await writer.WriteLineAsync($"* ENABLED {enabledStr}");
        await writer.WriteLineAsync($"{tag} OK ENABLE completed");
    }

    private async Task HandleSubscribeAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool subscribe, CancellationToken ct)
    {
        if (!TryParseMailboxName(args.Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }

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

    private async Task<List<MailboxFolderInfo>> GetUserFoldersAsync(
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
                f.IsSubscribed,
            })
            .OrderBy(f => f.Domain).ThenBy(f => f.InboxName).ThenBy(f => f.FolderName)
            .ToListAsync(ct);

        return folders
            .Select(folder => new MailboxFolderInfo(
                folder.InboxName,
                folder.Domain,
                folder.FolderName,
                string.Equals(
                    folder.OwnerUsername,
                    $"{folder.InboxName}@{folder.Domain}",
                    StringComparison.OrdinalIgnoreCase),
                folder.IsSubscribed))
            .ToList();
    }

    private static async Task<FolderDB?> ResolveFolderAsync(EmailDbContext db, Guid userId, string mailboxName, CancellationToken ct)
    {
        var location = await ResolveMailboxLocationAsync(db, userId, mailboxName, ct);
        if (location is null)
            return null;

        return await db.Folders
            .FirstOrDefaultAsync(folder => folder.InboxId == location.Value.InboxId
                                        && folder.Name == location.Value.FolderName, ct);
    }

    private static async Task<MailboxLocation?> ResolveMailboxLocationAsync(
        EmailDbContext db,
        Guid userId,
        string mailboxName,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mailboxName))
            return null;

        var username = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Username)
            .SingleOrDefaultAsync(ct);
        if (username is null)
            return null;

        var qualifiedParts = mailboxName.Split('/', 3);
        if (qualifiedParts.Length == 3)
        {
            var qualifiedInboxId = await db.Inboxes
                .AsNoTracking()
                .Where(inbox => inbox.OwnerId == userId
                             && inbox.Name == qualifiedParts[0]
                             && inbox.Address.Domain == qualifiedParts[1])
                .Select(inbox => (Guid?)inbox.Id)
                .SingleOrDefaultAsync(ct);
            if (qualifiedInboxId is not null)
                return new MailboxLocation(qualifiedInboxId.Value, qualifiedParts[2]);
        }

        var separator = username.LastIndexOf('@');
        if (separator <= 0 || separator == username.Length - 1)
            return null;

        var primaryLocalPart = username[..separator];
        var primaryDomain = username[(separator + 1)..];
        var primaryInboxId = await db.Inboxes
            .AsNoTracking()
            .Where(inbox => inbox.OwnerId == userId
                         && inbox.Name == primaryLocalPart
                         && inbox.Address.Domain == primaryDomain)
            .Select(inbox => (Guid?)inbox.Id)
            .SingleOrDefaultAsync(ct);
        return primaryInboxId is null
            ? null
            : new MailboxLocation(
                primaryInboxId.Value,
                NormalizePrimaryFolderName(mailboxName));
    }

    private static bool IsValidFolderName(string folderName)
    {
        if (folderName.Length is < 1 or > FolderDB.MaximumStoredNameLength
            || folderName[0] == '/'
            || folderName[^1] == '/'
            || folderName.Contains("//", StringComparison.Ordinal)
            || folderName.Any(char.IsControl))
        {
            return false;
        }

        var components = folderName.Split('/');
        return components.Length <= FolderDB.MaximumHierarchyDepth
            && components.All(component =>
                component.Length > 0
                && Encoding.UTF8.GetByteCount(component) <= FolderDB.MaximumLeafNameOctets);
    }

    private static bool IsSystemFolder(string folderName) =>
        DefaultFolders.All.Any(
            systemName => string.Equals(systemName, folderName, StringComparison.OrdinalIgnoreCase));

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
                Keywords = email.Keywords,
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
                RawMessage = source.RawMessage?.ToArray(),
                SizeBytes = source.SizeBytes,
                MessageId = source.MessageId,
                InReplyTo = source.InReplyTo,
                Cc = source.Cc,
                EmailObjectId = string.IsNullOrEmpty(source.EmailObjectId)
                    ? source.Id.ToString("N")
                    : source.EmailObjectId,
                ThreadObjectId = source.ThreadObjectId,
                IsRead = source.IsRead,
                IsDeleted = false,
                IsFlagged = source.IsFlagged,
                IsDraft = source.IsDraft,
                IsAnswered = source.IsAnswered,
                Keywords = source.Keywords.ToArray(),
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
            Keywords = metadata.Keywords.ToArray(),
            ModSeq = modSeq,
        };
        db.Emails.Attach(update);
        var entry = db.Entry(update);
        entry.Property(email => email.IsRead).IsModified = true;
        entry.Property(email => email.IsDeleted).IsModified = true;
        entry.Property(email => email.IsFlagged).IsModified = true;
        entry.Property(email => email.IsDraft).IsModified = true;
        entry.Property(email => email.IsAnswered).IsModified = true;
        entry.Property(email => email.Keywords).IsModified = true;
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

    private static IReadOnlyList<MailboxListEntry> BuildMailboxListEntries(
        IReadOnlyList<MailboxFolderInfo> folders)
    {
        var selectable = folders
            .Select(folder => (
                FullName: FormatMailboxName(
                    folder.InboxName,
                    folder.Domain,
                    folder.FolderName,
                    folder.IsPrimary),
                folder.FolderName,
                folder.IsSubscribed))
            .GroupBy(folder => folder.FullName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (
                    group.First().FolderName,
                    IsSubscribed: group.Any(folder => folder.IsSubscribed)),
                StringComparer.Ordinal);
        var names = new HashSet<string>(selectable.Keys, StringComparer.Ordinal);

        foreach (var fullName in selectable.Keys)
        {
            for (var index = fullName.IndexOf('/'); index >= 0; index = fullName.IndexOf('/', index + 1))
            {
                if (index > 0)
                    names.Add(fullName[..index]);
            }
        }

        return names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var isSelectable = selectable.TryGetValue(name, out var folder);
                var childPrefix = name + "/";
                var hasChildren = names.Any(
                    candidate => candidate.Length > childPrefix.Length
                              && candidate.StartsWith(childPrefix, StringComparison.Ordinal));
                return new MailboxListEntry(
                    name,
                    isSelectable ? folder.FolderName : null,
                    isSelectable,
                    hasChildren,
                    isSelectable && folder.IsSubscribed);
            })
            .ToList();
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

    private static string FormatMailboxObjectId(FolderDB folder) =>
        FormatObjectId('F', folder.MailboxId, folder.Id);

    private static string FormatEmailObjectId(EmailDB email) =>
        FormatEmailObjectId(email.Id, email.EmailObjectId);

    private static string FormatEmailObjectId(Guid id, string? emailObjectId) =>
        FormatObjectId('M', emailObjectId, id);

    private static string? FormatThreadObjectId(EmailDB email) =>
        FormatThreadObjectId(email.Id, email.ThreadObjectId);

    private static string? FormatThreadObjectId(Guid id, string? threadObjectId) =>
        string.IsNullOrEmpty(threadObjectId)
            ? null
            : FormatObjectId('T', threadObjectId, id);

    private static string FormatObjectId(char typePrefix, string? storedValue, Guid fallbackId)
    {
        var value = string.IsNullOrEmpty(storedValue)
            ? fallbackId.ToString("N")
            : storedValue;
        if (value.Length > 254 || !IsValidObjectId(value))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(value));
            value = Convert.ToHexStringLower(hash);
        }

        return string.Concat(typePrefix, value);
    }

    private static bool IsValidObjectId(string value) =>
        value.Length is >= 1 and <= 255
        && value.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_'
                or '-');

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

    private static string StripFetchList(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')'
            ? trimmed[1..^1].Trim()
            : trimmed;
    }

    private static bool ShouldSetSeen(
        string fetchItems,
        IReadOnlyList<BinaryFetchRequest> binaryRequests)
    {
        if (binaryRequests.Any(request =>
                request.Kind == BinaryFetchKind.Content && !request.Peek))
        {
            return true;
        }

        foreach (var item in TokenizeFetchDataItems(fetchItems))
        {
            if (item.Equals("RFC822", StringComparison.OrdinalIgnoreCase)
                || item.StartsWith("RFC822.TEXT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (item.StartsWith("BODY[", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool FetchNeedsStoredContent(
        string fetchItems,
        IReadOnlyCollection<BinaryFetchRequest> binaryRequests)
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
            || binaryRequests.Count > 0;
    }

    private static IReadOnlyList<string> TokenizeFetchDataItems(string fetchItems)
    {
        var items = new List<string>();
        var index = 0;
        while (index < fetchItems.Length)
        {
            while (index < fetchItems.Length && char.IsWhiteSpace(fetchItems[index]))
                index++;
            if (index >= fetchItems.Length)
                break;

            var start = index;
            var bracketDepth = 0;
            var parenthesisDepth = 0;
            var quoted = false;
            var escaped = false;
            while (index < fetchItems.Length)
            {
                var character = fetchItems[index];
                if (quoted)
                {
                    if (escaped)
                        escaped = false;
                    else if (character == '\\')
                        escaped = true;
                    else if (character == '"')
                        quoted = false;
                }
                else
                {
                    switch (character)
                    {
                        case '"':
                            quoted = true;
                            break;
                        case '[':
                            bracketDepth++;
                            break;
                        case ']':
                            if (bracketDepth > 0)
                                bracketDepth--;
                            break;
                        case '(':
                            parenthesisDepth++;
                            break;
                        case ')':
                            if (parenthesisDepth > 0)
                                parenthesisDepth--;
                            break;
                    }

                    if (char.IsWhiteSpace(character)
                        && bracketDepth == 0
                        && parenthesisDepth == 0)
                    {
                        break;
                    }
                }

                index++;
            }

            items.Add(fetchItems[start..index]);
        }

        return items;
    }

    private static bool TryParseBinaryFetchRequests(
        string fetchItems,
        out List<BinaryFetchRequest> requests)
    {
        requests = [];
        foreach (var item in TokenizeFetchDataItems(fetchItems))
        {
            var sizeMatch = BinarySizeDataItemRegex().Match(item);
            if (sizeMatch.Success)
            {
                requests.Add(new BinaryFetchRequest(
                    BinaryFetchKind.Size,
                    sizeMatch.Groups[1].Value,
                    Peek: true,
                    Offset: null,
                    Count: null));
                continue;
            }

            var contentMatch = BinaryContentDataItemRegex().Match(item);
            if (contentMatch.Success)
            {
                uint? offset = null;
                uint? count = null;
                if (contentMatch.Groups[3].Success)
                {
                    if (!uint.TryParse(contentMatch.Groups[3].ValueSpan, out var parsedOffset)
                        || !uint.TryParse(contentMatch.Groups[4].ValueSpan, out var parsedCount))
                    {
                        return false;
                    }
                    offset = parsedOffset;
                    count = parsedCount;
                }

                requests.Add(new BinaryFetchRequest(
                    BinaryFetchKind.Content,
                    contentMatch.Groups[2].Value,
                    Peek: contentMatch.Groups[1].Success,
                    offset,
                    count));
                continue;
            }

            if (item.StartsWith("BINARY", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool TryDecodeBinarySections(
        ImapMimeMessage? mimeMessage,
        IReadOnlyList<BinaryFetchRequest> requests,
        out Dictionary<string, ImapBinarySection> sections,
        out string failure)
    {
        sections = new Dictionary<string, ImapBinarySection>(StringComparer.Ordinal);
        failure = string.Empty;
        if (requests.Count == 0)
            return true;
        if (mimeMessage is null)
        {
            failure = "[CANNOT] Message MIME content cannot be parsed";
            return false;
        }

        foreach (var section in requests.Select(request => request.Section).Distinct(StringComparer.Ordinal))
        {
            var status = mimeMessage.GetBinarySection(section, out var content);
            switch (status)
            {
                case ImapBinarySectionStatus.Success:
                    sections.Add(section, content);
                    break;
                case ImapBinarySectionStatus.UnknownTransferEncoding:
                    failure = "[UNKNOWN-CTE] BINARY section uses an unknown transfer encoding";
                    return false;
                case ImapBinarySectionStatus.NotFound:
                case ImapBinarySectionStatus.NotLeaf:
                    failure = "[CANNOT] BINARY section does not name a leaf MIME part";
                    return false;
                default:
                    failure = "[CANNOT] BINARY section cannot be decoded";
                    return false;
            }
        }

        return true;
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
                Keywords = email.Keywords,
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

    private static string GetFolderAttributes(
        string? folderName,
        bool isSelectable,
        bool hasChildren,
        bool includeSubscribed = false,
        bool useNonExistent = false)
    {
        var attributes = new List<string>();
        if (!isSelectable)
            attributes.Add(useNonExistent ? "\\NonExistent" : "\\Noselect");

        var specialUse = GetSpecialUseAttribute(folderName);
        if (specialUse is not null)
            attributes.Add(specialUse);
        if (includeSubscribed)
            attributes.Add("\\Subscribed");

        attributes.Add(hasChildren ? "\\HasChildren" : "\\HasNoChildren");
        return string.Join(' ', attributes);
    }

    private static string? GetSpecialUseAttribute(string? folderName) => folderName switch
    {
        "Sent" => "\\Sent",
        "Drafts" => "\\Drafts",
        "Trash" => "\\Trash",
        "Spam" => "\\Junk",
        _ => null,
    };

    private static bool MatchesAnyPattern(
        string name,
        string reference,
        IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => pattern.Length > 0 && MatchesPattern(name, reference, pattern));

    private static bool MatchesListSelection(
        MailboxListEntry entry,
        ListCommandOptions options) =>
        (!options.SelectSubscribed || entry.IsSubscribed)
        && (!options.SelectSpecialUse || GetSpecialUseAttribute(entry.FolderName) is not null);

    private static string BuildChildInfoCriteria(ListCommandOptions options)
    {
        var criteria = new List<string>();
        if (options.SelectSubscribed)
            criteria.Add("\"SUBSCRIBED\"");
        if (options.SelectSpecialUse)
            criteria.Add("\"SPECIAL-USE\"");
        return string.Join(' ', criteria);
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

    private static string BuildFetchResponse(
        int seqNum,
        EmailDB email,
        string fetchItems,
        bool useUid,
        ImapMimeMessage? mimeMessage,
        IReadOnlyList<BinaryFetchRequest> binaryRequests,
        IReadOnlyDictionary<string, ImapBinarySection> binarySections)
    {
        var items = fetchItems.ToUpperInvariant();
        var normalizedItems = items.Replace("BODY.PEEK[", "BODY[");
        var requestedDataItems = TokenizeFetchDataItems(fetchItems);
        var parts = new List<string>();

        var numericSectionMatch = NumericBodySectionRegex().Match(items);
        var partialMatch = BodyPartialFetchRegex().Match(items);
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
            parts.Add($"BODYSTRUCTURE {mimeMessage?.BodyStructure ?? BuildFallbackBodyStructure(email, extended: true)}");
        }
        else if (BodyStandaloneRegex().IsMatch(normalizedItems))
        {
            parts.Add($"BODY {mimeMessage?.Body ?? BuildFallbackBodyStructure(email, extended: false)}");
        }

        if (numericSectionMatch.Success)
        {
            var section = numericSectionMatch.Groups[1].Value
                + numericSectionMatch.Groups[2].Value;
            string? sectionContent = null;
            if (mimeMessage?.TryGetSection(section, out var mimeContent) == true)
            {
                sectionContent = mimeContent;
            }
            else if (section == "1")
            {
                sectionContent = email.Body;
            }

            if (sectionContent is not null)
            {
                var (data, origin) = ApplyPartial(sectionContent, partialOffset, partialCount);
                var suffix = origin is not null ? $"<{origin}>" : string.Empty;
                parts.Add(
                    $"BODY[{section}]{suffix} {{{MailWireEncoding.Instance.GetByteCount(data)}}}\r\n{data}");
            }
        }

        if (requestedDataItems.Any(item => item.Equals("MODSEQ", StringComparison.OrdinalIgnoreCase)))
            parts.Add($"MODSEQ ({email.ModSeq})");

        if (requestedDataItems.Any(item => item.Equals("EMAILID", StringComparison.OrdinalIgnoreCase)))
            parts.Add($"EMAILID ({FormatEmailObjectId(email)})");

        if (requestedDataItems.Any(item => item.Equals("THREADID", StringComparison.OrdinalIgnoreCase)))
        {
            var threadId = FormatThreadObjectId(email);
            parts.Add(threadId is not null ? $"THREADID ({threadId})" : "THREADID NIL");
        }

        foreach (var request in binaryRequests)
        {
            var section = binarySections[request.Section];
            if (request.Kind == BinaryFetchKind.Size)
            {
                parts.Add($"BINARY.SIZE[{request.Section}] {section.Content.Length}");
                continue;
            }

            var (content, origin) = ApplyBinaryPartial(
                section.Content,
                request.Offset,
                request.Count);
            var suffix = origin is null ? string.Empty : $"<{origin}>";
            var literal8Marker = content.Span.Contains((byte)0) ? "~" : string.Empty;
            parts.Add(
                $"BINARY[{request.Section}]{suffix} {literal8Marker}{{{content.Length}}}\r\n" +
                MailWireEncoding.Instance.GetString(content.Span));
        }

        if (useUid || requestedDataItems.Any(item => item.Equals("UID", StringComparison.OrdinalIgnoreCase)))
            parts.Add($"UID {email.Uid}");

        return $"* {seqNum} FETCH ({string.Join(' ', parts)})";
    }

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[HEADER\.FIELDS\s*\(([^)]+)\)\]")]
    private static partial Regex HeaderFieldsRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[HEADER\.FIELDS\.NOT\s*\(([^)]+)\)\]")]
    private static partial Regex HeaderFieldsNotRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[((?:\d+\.)*\d+)(\.MIME)?\]")]
    private static partial Regex NumericBodySectionRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[[^\]]*\]<(\d+)\.(\d+)>")]
    private static partial Regex BodyPartialFetchRegex();

    [GeneratedRegex(@"(?<![.\[A-Z])BODY(?![.\[A-Z])")]
    private static partial Regex BodyStandaloneRegex();

    [GeneratedRegex(
        @"^BINARY(?:\.(PEEK))?\[((?:[1-9][0-9]*)(?:\.[1-9][0-9]*)*|)\](?:<([0-9]+)\.([1-9][0-9]*)>)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BinaryContentDataItemRegex();

    [GeneratedRegex(
        @"^BINARY\.SIZE\[((?:[1-9][0-9]*)(?:\.[1-9][0-9]*)*|)\]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BinarySizeDataItemRegex();

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

    private static (ReadOnlyMemory<byte> data, uint? origin) ApplyBinaryPartial(
        byte[] content,
        uint? offset,
        uint? count)
    {
        if (offset is null || count is null)
            return (content, null);

        var start = (int)Math.Min((long)offset.Value, content.Length);
        var available = content.Length - start;
        var length = (int)Math.Min((long)count.Value, available);
        return (content.AsMemory(start, length), offset);
    }

    private static string BuildFallbackBodyStructure(EmailDB email, bool extended)
    {
        var size = MailWireEncoding.Instance.GetByteCount(email.Body);
        var lines = email.Body.Length == 0
            ? 0
            : email.Body.Count(character => character == '\n')
                + (email.Body[^1] == '\n' ? 0 : 1);
        var structure =
            $"(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" {size} {lines}";
        if (extended)
            structure += " NIL NIL NIL NIL";
        return structure + ")";
    }

    private static string BuildFlagsList(EmailDB email)
    {
        var flags = new List<string>();
        if (email.IsRead) flags.Add("\\Seen");
        if (email.IsDeleted) flags.Add("\\Deleted");
        if (email.IsFlagged) flags.Add("\\Flagged");
        if (email.IsDraft) flags.Add("\\Draft");
        if (email.IsAnswered) flags.Add("\\Answered");
        flags.AddRange((email.Keywords ?? [])
            .Where(IsValidImapKeyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
        return string.Join(' ', flags);
    }

    private static string BuildRfc822(EmailDB email)
    {
        if (email.RawMessage is not null)
            return MailWireEncoding.Instance.GetString(email.RawMessage);
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
        if (email.RawMessage is not null)
        {
            var raw = MailWireEncoding.Instance.GetString(email.RawMessage);
            var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator >= 0)
                return raw[..(separator + 4)];
            separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
            if (separator >= 0)
                return raw[..(separator + 2)];
            return raw;
        }
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

    private static bool IsStoreAction(string action) =>
        action is "FLAGS" or "FLAGS.SILENT" or "+FLAGS" or "+FLAGS.SILENT" or "-FLAGS" or "-FLAGS.SILENT";

    private static bool TryValidateFlagList(IEnumerable<string> flags, out string failure)
    {
        foreach (var flag in flags)
        {
            if (IsMutableSystemFlag(flag))
                continue;

            if (flag.Equals("\\Recent", StringComparison.OrdinalIgnoreCase))
            {
                failure = "The \\Recent flag cannot be changed";
                return false;
            }

            if (flag.StartsWith('\\') || !IsValidImapKeyword(flag))
            {
                failure = "Invalid flag list";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static bool IsMutableSystemFlag(string flag) =>
        flag.ToUpperInvariant() is "\\SEEN" or "\\DELETED" or "\\FLAGGED" or "\\DRAFT" or "\\ANSWERED";

    private static bool IsValidImapKeyword(string keyword)
    {
        if (keyword.Length is 0 or > MaximumKeywordLength || keyword[0] == '\\')
            return false;

        foreach (var character in keyword)
        {
            if (character <= ' '
                || character >= '\u007f'
                || character is '(' or ')' or '{' or '%' or '*' or ']')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyFlags(
        EmailDB email,
        string action,
        IReadOnlyList<string> flags,
        out string failure)
    {
        if (!IsStoreAction(action))
        {
            failure = "Invalid STORE action";
            return false;
        }
        if (!TryValidateFlagList(flags, out failure))
            return false;

        var replace = action is "FLAGS" or "FLAGS.SILENT";
        var remove = action is "-FLAGS" or "-FLAGS.SILENT";
        var isRead = replace ? false : email.IsRead;
        var isDeleted = replace ? false : email.IsDeleted;
        var isFlagged = replace ? false : email.IsFlagged;
        var isDraft = replace ? false : email.IsDraft;
        var isAnswered = replace ? false : email.IsAnswered;
        var keywords = new HashSet<string>(
            replace
                ? []
                : (email.Keywords ?? []).Where(IsValidImapKeyword),
            StringComparer.OrdinalIgnoreCase);

        foreach (var flag in flags)
        {
            var value = !remove;
            switch (flag.ToUpperInvariant())
            {
                case "\\SEEN": isRead = value; break;
                case "\\DELETED": isDeleted = value; break;
                case "\\FLAGGED": isFlagged = value; break;
                case "\\DRAFT": isDraft = value; break;
                case "\\ANSWERED": isAnswered = value; break;
                default:
                    if (remove)
                        keywords.Remove(flag);
                    else
                        keywords.Add(flag);
                    break;
            }
        }

        if (keywords.Count > MaximumKeywordsPerMessage)
        {
            failure = "Too many keywords";
            return false;
        }

        email.IsRead = isRead;
        email.IsDeleted = isDeleted;
        email.IsFlagged = isFlagged;
        email.IsDraft = isDraft;
        email.IsAnswered = isAnswered;
        email.Keywords = keywords
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(keyword => keyword, StringComparer.Ordinal)
            .ToArray();
        failure = string.Empty;
        return true;
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

    private static bool TryResolveMessageSet(
        string value,
        int maximumIdentifier,
        IReadOnlyList<int> orderedUids,
        bool useUid,
        IReadOnlySet<int> savedSearchUids,
        out List<MessageSetRange> ranges)
    {
        if (!value.Equals("$", StringComparison.Ordinal))
            return TryParseMessageSet(value, maximumIdentifier, out ranges);

        ranges = [];
        for (var index = 0; index < orderedUids.Count; index++)
        {
            var uid = orderedUids[index];
            if (!savedSearchUids.Contains(uid))
                continue;

            var identifier = useUid ? uid : index + 1;
            if (ranges.Count > 0 && identifier == ranges[^1].End + 1)
            {
                ranges[^1] = new MessageSetRange(ranges[^1].Start, identifier);
            }
            else
            {
                ranges.Add(new MessageSetRange(identifier, identifier));
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

    private static bool TryParseListCommand(
        string args,
        bool utf8Enabled,
        out ListCommandOptions options,
        out string failureResponse)
    {
        options = new ListCommandOptions(
            string.Empty,
            [],
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            []);
        failureResponse = "Invalid LIST arguments";

        if (!TryTokenizeSearchCriteria(args, out var tokens) || tokens.Count == 0)
            return false;

        var index = 0;
        var isExtended = false;
        var selectionOptions = new List<string>();
        if (tokens[index].Kind == SearchTokenKind.OpenParenthesis)
        {
            isExtended = true;
            if (!TryReadFlatList(tokens, ref index, out selectionOptions))
                return false;
        }

        if (!TryReadListAtom(tokens, ref index, out var wireReference)
            || !TryParseMailboxName(wireReference, utf8Enabled, out var reference))
        {
            failureResponse = "Invalid LIST reference name";
            return false;
        }

        List<string> wirePatterns;
        if (index < tokens.Count && tokens[index].Kind == SearchTokenKind.OpenParenthesis)
        {
            isExtended = true;
            if (!TryReadFlatList(tokens, ref index, out wirePatterns))
                return false;
        }
        else if (TryReadListAtom(tokens, ref index, out var wirePattern))
        {
            wirePatterns = [wirePattern];
        }
        else
        {
            return false;
        }

        var patterns = new List<string>(wirePatterns.Count);
        foreach (var wirePattern in wirePatterns)
        {
            if (!TryParseMailboxName(wirePattern, utf8Enabled, out var pattern))
            {
                failureResponse = "Invalid LIST mailbox pattern";
                return false;
            }
            patterns.Add(pattern);
        }

        var returnSubscribed = false;
        var returnChildren = false;
        var returnSpecialUse = false;
        string[] statusItems = [];
        if (index < tokens.Count)
        {
            isExtended = true;
            if (!TryReadListAtom(tokens, ref index, out var returnKeyword)
                || !returnKeyword.Equals("RETURN", StringComparison.OrdinalIgnoreCase)
                || index >= tokens.Count
                || tokens[index].Kind != SearchTokenKind.OpenParenthesis)
            {
                return false;
            }

            index++;
            var sawReturnOption = false;
            var sawStatus = false;
            while (index < tokens.Count && tokens[index].Kind != SearchTokenKind.CloseParenthesis)
            {
                if (!TryReadListAtom(tokens, ref index, out var returnOption))
                    return false;

                sawReturnOption = true;
                switch (returnOption.ToUpperInvariant())
                {
                    case "SUBSCRIBED":
                        returnSubscribed = true;
                        break;
                    case "CHILDREN":
                        returnChildren = true;
                        break;
                    case "SPECIAL-USE":
                        returnSpecialUse = true;
                        break;
                    case "STATUS":
                        if (sawStatus
                            || !TryReadFlatList(tokens, ref index, out var requestedStatusItems)
                            || requestedStatusItems.Any(item => !IsSupportedStatusItem(item)))
                        {
                            failureResponse = "Invalid LIST STATUS items";
                            return false;
                        }

                        sawStatus = true;
                        statusItems = requestedStatusItems
                            .Select(item => item.ToUpperInvariant())
                            .ToArray();
                        break;
                    default:
                        failureResponse = $"Unsupported LIST return option {returnOption}";
                        return false;
                }
            }

            if (!sawReturnOption
                || index >= tokens.Count
                || tokens[index].Kind != SearchTokenKind.CloseParenthesis)
            {
                return false;
            }
            index++;
        }

        if (index != tokens.Count)
            return false;

        var selectSubscribed = false;
        var selectRemote = false;
        var selectRecursiveMatch = false;
        var selectSpecialUse = false;
        foreach (var selectionOption in selectionOptions)
        {
            switch (selectionOption.ToUpperInvariant())
            {
                case "SUBSCRIBED":
                    selectSubscribed = true;
                    break;
                case "REMOTE":
                    selectRemote = true;
                    break;
                case "RECURSIVEMATCH":
                    selectRecursiveMatch = true;
                    break;
                case "SPECIAL-USE":
                    selectSpecialUse = true;
                    break;
                default:
                    failureResponse = $"Unsupported LIST selection option {selectionOption}";
                    return false;
            }
        }

        if (selectRecursiveMatch && !selectSubscribed && !selectSpecialUse)
        {
            failureResponse = "RECURSIVEMATCH requires a filtering selection option";
            return false;
        }

        options = new ListCommandOptions(
            reference,
            patterns,
            isExtended,
            selectSubscribed,
            selectRemote,
            selectRecursiveMatch,
            selectSpecialUse,
            returnSubscribed,
            returnChildren,
            returnSpecialUse,
            statusItems);
        return true;
    }

    private static bool TryReadFlatList(
        IReadOnlyList<SearchToken> tokens,
        ref int index,
        out List<string> values)
    {
        values = [];
        if (index >= tokens.Count || tokens[index].Kind != SearchTokenKind.OpenParenthesis)
            return false;

        index++;
        while (index < tokens.Count && tokens[index].Kind != SearchTokenKind.CloseParenthesis)
        {
            if (!TryReadListAtom(tokens, ref index, out var value))
                return false;
            values.Add(value);
        }

        if (values.Count == 0
            || index >= tokens.Count
            || tokens[index].Kind != SearchTokenKind.CloseParenthesis)
        {
            return false;
        }

        index++;
        return true;
    }

    private static bool TryReadListAtom(
        IReadOnlyList<SearchToken> tokens,
        ref int index,
        out string value)
    {
        value = string.Empty;
        if (index >= tokens.Count || tokens[index].Kind != SearchTokenKind.Atom)
            return false;

        value = tokens[index++].Value;
        return true;
    }

    private static bool IsSupportedStatusItem(string item) =>
        item.ToUpperInvariant() is
            "MESSAGES" or
            "RECENT" or
            "UNSEEN" or
            "UIDVALIDITY" or
            "UIDNEXT" or
            "HIGHESTMODSEQ" or
            "SIZE" or
            "MAILBOXID";

    private static bool TryParseMailboxArgs(
        string args,
        bool utf8Enabled,
        out string reference,
        out string pattern)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 2
            || !TryParseMailboxName(tokens[0], utf8Enabled, out reference)
            || !TryParseMailboxName(tokens[1], utf8Enabled, out pattern))
        {
            reference = string.Empty;
            pattern = string.Empty;
            return false;
        }

        return true;
    }

    private static (string oldName, string newName)? ParseTwoMailboxArgs(
        string args,
        bool utf8Enabled)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 2) return null;
        if (!TryParseMailboxName(tokens[0], utf8Enabled, out var oldName)
            || !TryParseMailboxName(tokens[1], utf8Enabled, out var newName))
        {
            return null;
        }

        return (oldName, newName);
    }

    private static bool TryParseMailboxName(
        string value,
        bool utf8Enabled,
        out string mailboxName)
    {
        var wireName = UnquoteArg(value);
        if (!utf8Enabled)
            return ImapMailboxEncoding.TryDecode(wireName, out mailboxName);

        if (!TryDecodeUtf8WireValue(wireName, out mailboxName)
            || !IsValidNetUnicodeMailboxName(mailboxName))
        {
            mailboxName = string.Empty;
            return false;
        }

        mailboxName = mailboxName.Normalize(NormalizationForm.FormC);
        return true;
    }

    private static string FormatWireMailboxName(string mailboxName, bool utf8Enabled)
    {
        if (!utf8Enabled)
            return ImapMailboxEncoding.Encode(mailboxName);

        return ProtocolEncoding.GetString(
            StrictUtf8.GetBytes(mailboxName.Normalize(NormalizationForm.FormC)));
    }

    private static bool TryDecodeUtf8WireValue(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Any(character => character > byte.MaxValue))
            return false;

        try
        {
            decoded = StrictUtf8.GetString(ProtocolEncoding.GetBytes(value));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidNetUnicodeMailboxName(string value) =>
        value.All(character => character is not (>= '\u0000' and <= '\u001f')
            and not '\u007f'
            and not (>= '\u0080' and <= '\u009f')
            and not '\u2028'
            and not '\u2029');

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
        if (!TryParseMailboxName(args.Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name");
            return;
        }

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

        await writer.WriteLineAsync(
            $"* QUOTAROOT \"{EscapeImapString(FormatWireMailboxName(mailboxName, session.Utf8Enabled))}\" \"\"");
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
        ModSequenceResult = 4,
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
        string[] Keywords,
        long ModSeq,
        int SizeBytes,
        string? RawHeaders,
        string? MessageId,
        string? InReplyTo,
        string? Cc,
        string? EmailObjectId,
        string? ThreadObjectId,
        DateTime ReceivedAt);

    private sealed record SearchCandidate(Guid Id, int Uid, int SequenceNumber);

    private sealed record SearchExecutionResult(
        IReadOnlyList<SearchCandidate> Matches,
        string? FailureResponse,
        long? HighestModSequence);

    private enum ImapSortKey
    {
        Arrival,
        Cc,
        Date,
        From,
        Size,
        Subject,
        To,
    }

    private readonly record struct ImapSortCriterion(ImapSortKey Key, bool Reverse);

    private sealed record SortStoredMessage(
        Guid Id,
        int Uid,
        int SequenceNumber,
        DateTime ReceivedAt,
        int SizeBytes,
        string Sender,
        string Recipient,
        string? Cc,
        string Subject,
        string? RawHeaders);

    private sealed record SortMessage(
        int Uid,
        int SequenceNumber,
        DateTime ReceivedAt,
        DateTime SentAt,
        int SizeBytes,
        byte[] FromSortKey,
        byte[] ToSortKey,
        byte[] CcSortKey,
        byte[] SubjectSortKey);

    private sealed record ThreadStoredMessage(
        Guid Id,
        int Uid,
        int SequenceNumber,
        DateTime ReceivedAt,
        string Subject,
        string? RawHeaders,
        string? MessageId,
        string? InReplyTo);

    private sealed record SearchPredicate(
        SearchDataRequirements Requirements,
        Func<SearchStoredMessage, int, bool> IsMatch);

    private sealed class SearchParser(
        IReadOnlyList<SearchToken> tokens,
        int maximumSequenceNumber,
        int maximumUid,
        IReadOnlySet<int> savedSearchUids,
        bool utf8Enabled)
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

                var supportedCharset = utf8Enabled ? "UTF-8" : "US-ASCII";
                if (!charset.Equals(supportedCharset, StringComparison.OrdinalIgnoreCase))
                {
                    failureResponse = utf8Enabled
                        ? "BAD SEARCH charset conflicts with UTF8=ACCEPT"
                        : "NO [BADCHARSET (US-ASCII)] Unsupported search charset";
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

            if (token.Value == "$")
            {
                predicate = new SearchPredicate(
                    SearchDataRequirements.None,
                    (message, _) => savedSearchUids.Contains(message.Uid));
                return true;
            }

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
                case "EMAILID":
                    if (!TryReadValue(out var emailObjectId)
                        || !IsValidObjectId(emailObjectId))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => string.Equals(
                            FormatEmailObjectId(message.Id, message.EmailObjectId),
                            emailObjectId,
                            StringComparison.Ordinal));
                    return true;
                case "THREADID":
                    if (!TryReadValue(out var threadObjectId)
                        || !IsValidObjectId(threadObjectId))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => string.Equals(
                            FormatThreadObjectId(message.Id, message.ThreadObjectId),
                            threadObjectId,
                            StringComparison.Ordinal));
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
                    if (!TryReadValue(out var uidSet))
                    {
                        return false;
                    }

                    if (uidSet == "$")
                    {
                        predicate = new SearchPredicate(
                            SearchDataRequirements.None,
                            (message, _) => savedSearchUids.Contains(message.Uid));
                        return true;
                    }

                    if (!TryParseMessageSet(uidSet, maximumUid, out var uidRanges))
                        return false;

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
                    if (!TryReadValue(out var modSequenceText))
                        return false;
                    if (!TryParseModSequence(modSequenceText, out var modSequence))
                    {
                        if (!modSequenceText.StartsWith("/flags/", StringComparison.OrdinalIgnoreCase)
                            || !TryReadValue(out var entryType)
                            || entryType.ToUpperInvariant() is not ("SHARED" or "PRIV" or "ALL")
                            || !TryReadValue(out modSequenceText)
                            || !TryParseModSequence(modSequenceText, out modSequence))
                        {
                            return false;
                        }
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.ModSequenceResult,
                        (message, _) => message.ModSeq >= modSequence);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseModSequence(string value, out long modSequence) =>
            long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out modSequence)
            && modSequence >= 0;

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
        IReadOnlySet<int> savedSearchUids,
        bool utf8Enabled,
        CancellationToken cancellationToken,
        string? explicitCharset = null)
    {
        var decodeUtf8 = explicitCharset?.Equals(
            "UTF-8",
            StringComparison.OrdinalIgnoreCase) == true || explicitCharset is null && utf8Enabled;
        if (decodeUtf8 && !TryDecodeUtf8WireValue(criteria, out criteria))
        {
            return new SearchExecutionResult(
                [],
                "BAD Invalid UTF-8 in search criteria",
                null);
        }
        if (explicitCharset?.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase) == true
            && criteria.Any(character => character > '\u007f'))
        {
            return new SearchExecutionResult(
                [],
                "BAD Invalid US-ASCII in search criteria",
                null);
        }

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
                "BAD Invalid search criteria",
                null);
        }

        var parser = new SearchParser(
            tokens,
            maximumSequenceNumber,
            maximumUid,
            savedSearchUids,
            utf8Enabled);
        if (!parser.TryParse(out var predicate, out var failureResponse))
            return new SearchExecutionResult([], failureResponse, null);

        var includeBody = predicate.Requirements.HasFlag(SearchDataRequirements.Body);
        var includeRawHeaders = predicate.Requirements.HasFlag(SearchDataRequirements.RawHeaders);
        var includeModSequence = predicate.Requirements.HasFlag(
            SearchDataRequirements.ModSequenceResult);
        var messageQuery = BuildSearchMessageQuery(query, includeBody, includeRawHeaders);
        var matches = new List<SearchCandidate>();
        long? highestModSequence = null;
        var sequenceNumber = 0;
        await foreach (var message in messageQuery
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken))
        {
            sequenceNumber++;
            if (predicate.IsMatch(message, sequenceNumber))
            {
                matches.Add(new SearchCandidate(message.Id, message.Uid, sequenceNumber));
                if (includeModSequence
                    && (highestModSequence is null || message.ModSeq > highestModSequence))
                {
                    highestModSequence = message.ModSeq;
                }
            }
        }

        return new SearchExecutionResult(matches, null, highestModSequence);
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
                message.Keywords,
                message.ModSeq,
                message.SizeBytes,
                message.RawHeaders,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.EmailObjectId,
                message.ThreadObjectId,
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
                message.Keywords,
                message.ModSeq,
                message.SizeBytes,
                null,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.EmailObjectId,
                message.ThreadObjectId,
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
                message.Keywords,
                message.ModSeq,
                message.SizeBytes,
                message.RawHeaders,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.EmailObjectId,
                message.ThreadObjectId,
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
            message.Keywords,
            message.ModSeq,
            message.SizeBytes,
            null,
            message.MessageId,
            message.InReplyTo,
            message.Cc,
            message.EmailObjectId,
            message.ThreadObjectId,
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
            while (index < criteria.Length && criteria[index] == ' ')
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
                    && criteria[index] != ' '
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
                    && criteria[index] != ' '
                    && criteria[index] != ')')
                {
                    return false;
                }

                tokens.Add(new SearchToken(SearchTokenKind.Atom, value.ToString()));
                continue;
            }

            var start = index;
            while (index < criteria.Length
                   && criteria[index] != ' '
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
            _ => message.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase),
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

    private static bool StartsWithCharsetSearchKey(string criteria)
    {
        var trimmed = criteria.TrimStart();
        return trimmed.Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("CHARSET ", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<int> SelectSavedSearchUids(
        IReadOnlyCollection<string> returnOptions,
        IReadOnlyList<SearchCandidate> matches)
    {
        var saveAll = returnOptions.Contains("ALL")
            || returnOptions.Contains("COUNT")
            || !returnOptions.Contains("MIN") && !returnOptions.Contains("MAX");
        if (saveAll)
            return matches.Select(match => match.Uid).ToHashSet();

        var saved = new HashSet<int>();
        if (matches.Count == 0)
            return saved;

        if (returnOptions.Contains("MIN"))
            saved.Add(matches[0].Uid);
        if (returnOptions.Contains("MAX"))
            saved.Add(matches[^1].Uid);
        return saved;
    }

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

    private static string BuildEsearchResult(
        string[] returnOpts,
        List<int> numbers,
        long? highestModSequence)
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
        if (highestModSequence is { } value)
            parts.Add($"MODSEQ {value}");

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
        if (!TryParseSortArguments(
                args,
                out var sortCriteria,
                out var charset,
                out var searchCriteria))
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        if (!charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync(
                $"{tag} NO [BADCHARSET (US-ASCII UTF-8)] Unsupported sort charset");
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
        var searchResult = await FindSearchCandidatesAsync(
            folderQuery,
            searchCriteria,
            session.SavedSearchUids,
            session.Utf8Enabled,
            ct,
            charset);
        if (searchResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}");
            return;
        }

        var matchedIds = searchResult.Matches.Select(candidate => candidate.Id).ToArray();
        var sequenceById = searchResult.Matches.ToDictionary(
            candidate => candidate.Id,
            candidate => candidate.SequenceNumber);
        var storedMessages = await folderQuery
            .Where(email => matchedIds.Contains(email.Id))
            .Select(email => new SortStoredMessage(
                email.Id,
                email.Uid,
                0,
                email.ReceivedAt,
                email.SizeBytes,
                email.Sender,
                email.Recipient,
                email.Cc,
                email.Subject,
                email.RawHeaders))
            .ToListAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var messages = storedMessages
            .Select(message => CreateSortMessage(
                message with { SequenceNumber = sequenceById[message.Id] }))
            .ToList();
        messages.Sort((left, right) => CompareSortMessages(left, right, sortCriteria));

        var result = string.Join(
            ' ',
            messages.Select(message => useUid ? message.Uid : message.SequenceNumber));
        var resultSuffix = result.Length == 0 ? string.Empty : $" {result}";
        var modSequenceSuffix = searchResult.HighestModSequence is { } highestModSequence
            ? $" (MODSEQ {highestModSequence})"
            : string.Empty;
        await writer.WriteLineAsync($"* SORT{resultSuffix}{modSequenceSuffix}");
        await writer.WriteLineAsync($"{tag} OK {(useUid ? "UID SORT" : "SORT")} completed");
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
        if (!TryParseThreadArguments(
                args,
                out var algorithm,
                out var charset,
                out var searchCriteria))
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error");
            return;
        }

        if (algorithm is not "REFERENCES" and not "ORDEREDSUBJECT")
        {
            await writer.WriteLineAsync($"{tag} BAD Unknown threading algorithm");
            return;
        }
        if (!charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync(
                $"{tag} NO [BADCHARSET (US-ASCII UTF-8)] Unsupported thread charset");
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
        var searchResult = await FindSearchCandidatesAsync(
            folderQuery,
            searchCriteria,
            session.SavedSearchUids,
            session.Utf8Enabled,
            ct,
            charset);
        if (searchResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}");
            return;
        }

        var matchedIds = searchResult.Matches.Select(candidate => candidate.Id).ToArray();

        if (algorithm == "ORDEREDSUBJECT")
        {
            var sequenceById = searchResult.Matches.ToDictionary(
                candidate => candidate.Id,
                candidate => candidate.SequenceNumber);
            var storedMessages = await folderQuery
                .Where(email => matchedIds.Contains(email.Id))
                .Select(email => new SortStoredMessage(
                    email.Id,
                    email.Uid,
                    0,
                    email.ReceivedAt,
                    email.SizeBytes,
                    email.Sender,
                    email.Recipient,
                    email.Cc,
                    email.Subject,
                    email.RawHeaders))
                .ToListAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            var messages = storedMessages
                .Select(message => CreateSortMessage(
                    message with { SequenceNumber = sequenceById[message.Id] }))
                .ToList();
            var threads = BuildOrderedSubjectThreads(messages, useUid);
            var responseSuffix = threads.Length == 0 ? string.Empty : $" {threads}";
            await writer.WriteLineAsync($"* THREAD{responseSuffix}");
            await writer.WriteLineAsync(
                $"{tag} OK {(useUid ? "UID THREAD" : "THREAD")} completed");
            return;
        }

        var referenceSequenceById = searchResult.Matches.ToDictionary(
            candidate => candidate.Id,
            candidate => candidate.SequenceNumber);
        var referenceStoredMessages = await folderQuery
            .Where(email => matchedIds.Contains(email.Id))
            .Select(email => new ThreadStoredMessage(
                email.Id,
                email.Uid,
                0,
                email.ReceivedAt,
                email.Subject,
                email.RawHeaders,
                email.MessageId,
                email.InReplyTo))
            .ToListAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        var referenceMessages = referenceStoredMessages
            .Select(message => CreateReferenceThreadMessage(
                message with { SequenceNumber = referenceSequenceById[message.Id] },
                useUid))
            .ToArray();
        var referenceThreads = Rfc5256Threading.BuildReferences(referenceMessages);
        var referenceSuffix = referenceThreads.Length == 0
            ? string.Empty
            : $" {referenceThreads}";
        await writer.WriteLineAsync($"* THREAD{referenceSuffix}");
        await writer.WriteLineAsync(
            $"{tag} OK {(useUid ? "UID THREAD" : "THREAD")} completed");
    }

    private static bool TryParseSortArguments(
        string args,
        out IReadOnlyList<ImapSortCriterion> criteria,
        out string charset,
        out string searchCriteria)
    {
        criteria = [];
        charset = string.Empty;
        searchCriteria = string.Empty;

        var value = args.TrimStart(' ');
        if (value.Length < 3 || value[0] != '(')
            return false;
        var closeParen = value.IndexOf(')');
        if (closeParen <= 1
            || closeParen + 1 >= value.Length
            || value[closeParen + 1] != ' '
            || value.AsSpan(1, closeParen - 1).Contains('('))
            return false;

        var tokens = value[1..closeParen]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > MaximumSearchTokens)
            return false;

        var parsed = new List<ImapSortCriterion>(tokens.Length);
        for (var index = 0; index < tokens.Length; index++)
        {
            var reverse = tokens[index].Equals("REVERSE", StringComparison.OrdinalIgnoreCase);
            if (reverse && ++index >= tokens.Length)
                return false;

            var key = tokens[index].ToUpperInvariant() switch
            {
                "ARRIVAL" => ImapSortKey.Arrival,
                "CC" => ImapSortKey.Cc,
                "DATE" => ImapSortKey.Date,
                "FROM" => ImapSortKey.From,
                "SIZE" => ImapSortKey.Size,
                "SUBJECT" => ImapSortKey.Subject,
                "TO" => ImapSortKey.To,
                _ => (ImapSortKey?)null,
            };
            if (key is null)
                return false;
            parsed.Add(new ImapSortCriterion(key.Value, reverse));
        }

        var remainder = value[(closeParen + 1)..].TrimStart(' ');
        if (!TryReadCharsetAndSearchCriteria(remainder, out charset, out searchCriteria))
            return false;

        criteria = parsed;
        return true;
    }

    private static bool TryParseThreadArguments(
        string args,
        out string algorithm,
        out string charset,
        out string searchCriteria)
    {
        algorithm = string.Empty;
        charset = string.Empty;
        searchCriteria = string.Empty;

        var value = args.TrimStart(' ');
        var separator = value.IndexOf(' ');
        if (separator <= 0)
            return false;

        algorithm = value[..separator].ToUpperInvariant();
        return TryReadCharsetAndSearchCriteria(
            value[(separator + 1)..].TrimStart(' '),
            out charset,
            out searchCriteria);
    }

    private static bool TryReadCharsetAndSearchCriteria(
        string value,
        out string charset,
        out string searchCriteria)
    {
        charset = string.Empty;
        searchCriteria = string.Empty;
        if (value.Length == 0)
            return false;

        var index = 0;
        if (value[index] == '"')
        {
            index++;
            var parsed = new StringBuilder();
            var terminated = false;
            while (index < value.Length)
            {
                var character = value[index++];
                if (character == '"')
                {
                    terminated = true;
                    break;
                }
                if (character == '\\')
                {
                    if (index >= value.Length || value[index] is not ('\\' or '"'))
                        return false;
                    character = value[index++];
                }
                if (character is '\r' or '\n' or '\0')
                    return false;
                parsed.Append(character);
            }
            if (!terminated)
                return false;
            charset = parsed.ToString();
        }
        else
        {
            var start = index;
            while (index < value.Length && value[index] != ' ')
            {
                var character = value[index];
                if (character <= ' '
                    || character >= '\u007f'
                    || character is '(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']')
                {
                    return false;
                }
                index++;
            }
            if (index == start)
                return false;
            charset = value[start..index];
        }

        if (index >= value.Length || value[index] != ' ')
            return false;
        while (index < value.Length && value[index] == ' ')
            index++;
        if (index >= value.Length)
            return false;
        searchCriteria = value[index..];
        return true;
    }

    private static string BuildOrderedSubjectThreads(
        List<SortMessage> messages,
        bool useUid)
    {
        messages.Sort(static (left, right) =>
        {
            var subjectComparison = left.SubjectSortKey.AsSpan().SequenceCompareTo(
                right.SubjectSortKey);
            return subjectComparison != 0
                ? subjectComparison
                : CompareThreadSentDate(left, right);
        });

        var groups = new List<List<SortMessage>>();
        for (var index = 0; index < messages.Count;)
        {
            var group = new List<SortMessage> { messages[index++] };
            while (index < messages.Count
                   && group[0].SubjectSortKey.AsSpan().SequenceEqual(
                       messages[index].SubjectSortKey))
            {
                group.Add(messages[index++]);
            }
            groups.Add(group);
        }
        groups.Sort(static (left, right) => CompareThreadSentDate(left[0], right[0]));

        var result = new StringBuilder();
        foreach (var group in groups)
        {
            result.Append('(').Append(ThreadIdentifier(group[0], useUid));
            if (group.Count == 2)
            {
                result.Append(' ').Append(ThreadIdentifier(group[1], useUid));
            }
            else if (group.Count > 2)
            {
                result.Append(' ');
                for (var index = 1; index < group.Count; index++)
                {
                    result.Append('(')
                        .Append(ThreadIdentifier(group[index], useUid))
                        .Append(')');
                }
            }
            result.Append(')');
        }
        return result.ToString();
    }

    private static int CompareThreadSentDate(SortMessage left, SortMessage right)
    {
        var dateComparison = left.SentAt.CompareTo(right.SentAt);
        return dateComparison != 0
            ? dateComparison
            : left.SequenceNumber.CompareTo(right.SequenceNumber);
    }

    private static int ThreadIdentifier(SortMessage message, bool useUid) =>
        useUid ? message.Uid : message.SequenceNumber;

    private static Rfc5256ThreadMessage CreateReferenceThreadMessage(
        ThreadStoredMessage stored,
        bool useUid)
    {
        var sentAt = stored.ReceivedAt;
        var subject = stored.Subject;
        var messageId = Rfc5256Threading.ParseFirstMessageId(stored.MessageId);
        IReadOnlyList<string> references = Rfc5256Threading.ParseMessageIds(stored.InReplyTo)
            .Take(1)
            .ToArray();

        if (!string.IsNullOrEmpty(stored.RawHeaders))
        {
            try
            {
                var rawHeaders = stored.RawHeaders.EndsWith("\r\n\r\n", StringComparison.Ordinal)
                    || stored.RawHeaders.EndsWith("\n\n", StringComparison.Ordinal)
                    ? stored.RawHeaders
                    : stored.RawHeaders + "\r\n\r\n";
                using var stream = new MemoryStream(
                    MailWireEncoding.Instance.GetBytes(rawHeaders),
                    writable: false);
                using var message = MimeMessage.Load(stream, persistent: false);
                subject = message.Subject ?? string.Empty;
                var dateHeader = message.Headers.FirstOrDefault(header =>
                    header.Field.Equals("Date", StringComparison.OrdinalIgnoreCase));
                if (dateHeader is not null
                    && MimeKit.Utils.DateUtils.TryParse(dateHeader.Value, out var parsedDate))
                {
                    sentAt = parsedDate.UtcDateTime;
                }

                messageId = MessageIdsFromHeaders(message, "Message-ID")
                    .FirstOrDefault();
                var headerReferences = MessageIdsFromHeaders(message, "References");
                references = headerReferences.Count > 0
                    ? headerReferences
                    : MessageIdsFromHeaders(message, "In-Reply-To")
                        .Take(1)
                        .ToArray();
            }
            catch (Exception exception) when (
                exception is FormatException or IOException or ParseException)
            {
                // Legacy rows can contain malformed raw headers. Their normalized
                // columns remain a deterministic fallback for REFERENCES.
            }
        }

        var analyzedSubject = Rfc5256.AnalyzeSubject(subject);
        return new Rfc5256ThreadMessage(
            useUid ? stored.Uid : stored.SequenceNumber,
            stored.SequenceNumber,
            sentAt,
            Convert.ToBase64String(
                Rfc5256.UnicodeCasemapSortKey(analyzedSubject.BaseSubject)),
            analyzedSubject.IsReplyOrForward,
            messageId,
            references);
    }

    private static IReadOnlyList<string> MessageIdsFromHeaders(
        MimeMessage message,
        string fieldName)
    {
        var result = new List<string>();
        foreach (var header in message.Headers)
        {
            if (!header.Field.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            result.AddRange(Rfc5256Threading.ParseMessageIds(header.Value));
        }
        return result;
    }

    private static SortMessage CreateSortMessage(SortStoredMessage stored)
    {
        var sentAt = stored.ReceivedAt;
        var from = FirstMailboxLocalPart(stored.Sender);
        var to = FirstMailboxLocalPart(stored.Recipient);
        var cc = FirstMailboxLocalPart(stored.Cc);
        var subject = stored.Subject;

        if (!string.IsNullOrEmpty(stored.RawHeaders))
        {
            try
            {
                var rawHeaders = stored.RawHeaders.EndsWith("\r\n\r\n", StringComparison.Ordinal)
                    || stored.RawHeaders.EndsWith("\n\n", StringComparison.Ordinal)
                    ? stored.RawHeaders
                    : stored.RawHeaders + "\r\n\r\n";
                using var stream = new MemoryStream(
                    MailWireEncoding.Instance.GetBytes(rawHeaders),
                    writable: false);
                using var message = MimeMessage.Load(stream, persistent: false);
                from = FirstMailboxLocalPart(message.From);
                to = FirstMailboxLocalPart(message.To);
                cc = FirstMailboxLocalPart(message.Cc);
                subject = message.Subject ?? string.Empty;
                var dateHeader = message.Headers.FirstOrDefault(header =>
                    header.Field.Equals("Date", StringComparison.OrdinalIgnoreCase));
                if (dateHeader is not null
                    && MimeKit.Utils.DateUtils.TryParse(dateHeader.Value, out var parsedDate))
                {
                    sentAt = parsedDate.UtcDateTime;
                }
            }
            catch (Exception exception) when (
                exception is FormatException or IOException or ParseException)
            {
                // Legacy rows can contain malformed raw headers. Their normalized
                // columns remain a deterministic fallback for SORT and THREAD.
            }
        }

        return new SortMessage(
            stored.Uid,
            stored.SequenceNumber,
            stored.ReceivedAt,
            sentAt,
            stored.SizeBytes,
            Rfc5256.UnicodeCasemapSortKey(from),
            Rfc5256.UnicodeCasemapSortKey(to),
            Rfc5256.UnicodeCasemapSortKey(cc),
            Rfc5256.UnicodeCasemapSortKey(Rfc5256.BaseSubject(subject)));
    }

    private static string FirstMailboxLocalPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !InternetAddressList.TryParse(value, out var addresses))
        {
            return string.Empty;
        }
        return FirstMailboxLocalPart(addresses);
    }

    private static string FirstMailboxLocalPart(InternetAddressList addresses)
    {
        var address = addresses.Mailboxes.FirstOrDefault()?.Address;
        if (string.IsNullOrEmpty(address))
            return string.Empty;
        var separator = address.LastIndexOf('@');
        return separator > 0 ? address[..separator] : address;
    }

    private static int CompareSortMessages(
        SortMessage left,
        SortMessage right,
        IReadOnlyList<ImapSortCriterion> criteria)
    {
        foreach (var criterion in criteria)
        {
            var comparison = criterion.Key switch
            {
                ImapSortKey.Arrival => left.ReceivedAt.CompareTo(right.ReceivedAt),
                ImapSortKey.Cc => left.CcSortKey.AsSpan().SequenceCompareTo(right.CcSortKey),
                ImapSortKey.Date => left.SentAt.CompareTo(right.SentAt),
                ImapSortKey.From => left.FromSortKey.AsSpan().SequenceCompareTo(right.FromSortKey),
                ImapSortKey.Size => left.SizeBytes.CompareTo(right.SizeBytes),
                ImapSortKey.Subject => left.SubjectSortKey.AsSpan().SequenceCompareTo(
                    right.SubjectSortKey),
                ImapSortKey.To => left.ToSortKey.AsSpan().SequenceCompareTo(right.ToSortKey),
                _ => 0,
            };
            if (comparison == 0)
                continue;
            if (!criterion.Reverse)
                return comparison;
            return comparison < 0 ? 1 : -1;
        }

        return left.SequenceNumber.CompareTo(right.SequenceNumber);
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
        if (!TryResolveMessageSet(
                uidSetArg,
                maxUid,
                emails.Select(email => email.Uid).ToList(),
                useUid: true,
                session.SavedSearchUids,
                out var parsedMessageSet))
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
        if (!TryParseMailboxName(args[(spaceIdx + 1)..].Trim(), session.Utf8Enabled, out var destMailbox))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid destination mailbox name");
            return;
        }

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
        if (!TryResolveMessageSet(
                messageSet,
                maximumIdentifier,
                emails.Select(email => email.Uid).ToList(),
                useUid,
                session.SavedSearchUids,
                out var parsedMessageSet))
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

        var commandName = useUid ? "UID MOVE" : "MOVE";
        if (selected.Count == 0)
        {
            await writer.WriteLineAsync($"{tag} OK {commandName} completed");
            return;
        }

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
            var (mailboxName, flags, internalDate, literalSize, isLiteral8, isLiteralPlus) = ParseAppendArgs(
                remaining,
                session.Utf8Enabled);

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

            if (!TryValidateFlagList(flags, out var flagFailure))
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    $"[CANNOT] {flagFailure}");
                return;
            }

            var keywordCount = flags
                .Where(flag => !flag.StartsWith('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (keywordCount > MaximumKeywordsPerMessage)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "[LIMIT] APPEND contains too many keywords");
                return;
            }

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

            if (literalSize == 0)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "APPEND requires a non-empty message");
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
                if (!await ConsumeRejectedAppendRemainderAsync(
                        reader,
                        writer,
                        session,
                        timeout,
                        connectionTimeoutSeconds,
                        ct))
                {
                    return;
                }
                var response = isLiteral8
                    ? "[UNKNOWN-CTE] Binary APPEND storage is not supported"
                    : "APPEND content contains a NUL byte";
                await writer.WriteLineAsync($"{tag} NO {response}");
                return;
            }

            if (!TryPrepareAppendMessageForParsing(
                    messageData,
                    session.Utf8Enabled,
                    out var messageForParsing,
                    out var utf8Failure))
            {
                if (!await ConsumeRejectedAppendRemainderAsync(
                        reader,
                        writer,
                        session,
                        timeout,
                        connectionTimeoutSeconds,
                        ct))
                {
                    return;
                }
                await writer.WriteLineAsync($"{tag} NO [CANNOT] {utf8Failure}");
                return;
            }

            var (subject, body, headers) = MailMessageParser.Parse(messageForParsing);
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
                RawMessage = MailWireEncoding.Instance.GetBytes(messageData),
                SizeBytes = MailWireEncoding.Instance.GetByteCount(messageData),
                MessageId = msgId,
                InReplyTo = inReplyTo,
                Cc = MailMessageParser.ExtractHeaderValue(headers, "Cc"),
                EmailObjectId = Guid.CreateVersion7().ToString("N"),
                ThreadObjectId = GenerateThreadObjectId(inReplyTo, msgId),
                ReceivedAt = internalDate ?? DateTime.UtcNow,
            };

            if (!TryApplyFlags(email, "+FLAGS", flags, out var applyFailure))
            {
                await writer.WriteLineAsync($"{tag} NO [LIMIT] {applyFailure}");
                return;
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
            if (nextLine is null || nextLine.Length == 0)
                break;

            if (!nextLine.TrimStart().StartsWith('(')
                && !nextLine.TrimStart().StartsWith('{')
                && !nextLine.TrimStart().StartsWith("~{", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync($"{tag} BAD Invalid APPEND continuation");
                return;
            }

            remaining =
                $"\"{EscapeImapString(FormatWireMailboxName(targetMailbox, session.Utf8Enabled))}\" {nextLine.Trim()}";
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

    private static bool TryPrepareAppendMessageForParsing(
        string messageData,
        bool utf8Enabled,
        out string messageForParsing,
        out string failure)
    {
        messageForParsing = messageData;
        failure = string.Empty;

        if (utf8Enabled && TryDecodeUtf8WireValue(messageData, out var decodedMessage))
        {
            messageForParsing = decodedMessage;
            return true;
        }

        var separatorIndex = messageData.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separatorIndex < 0)
            separatorIndex = messageData.IndexOf("\n\n", StringComparison.Ordinal);

        var headerLength = separatorIndex >= 0 ? separatorIndex : messageData.Length;
        var wireHeaders = messageData[..headerLength];
        if (!wireHeaders.Any(character => character > 0x7f))
            return true;

        if (!utf8Enabled)
        {
            failure = "Internationalized headers require ENABLE UTF8=ACCEPT";
            return false;
        }

        if (!TryDecodeUtf8WireValue(wireHeaders, out var decodedHeaders))
        {
            failure = "Message headers are not valid UTF-8";
            return false;
        }

        messageForParsing = decodedHeaders + messageData[headerLength..];
        return true;
    }

    private static async Task<bool> ConsumeRejectedAppendRemainderAsync(
        BoundedLineReader reader,
        StreamWriter writer,
        ImapSession session,
        CancellationTokenSource timeout,
        int connectionTimeoutSeconds,
        CancellationToken ct)
    {
        timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
        var remainder = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct);
        if (remainder.IsTooLong)
        {
            await writer.WriteLineAsync("* BYE APPEND continuation is too long");
            session.State = ImapState.Logout;
            return false;
        }

        if (remainder.Value is null)
        {
            session.State = ImapState.Logout;
            return false;
        }

        if (remainder.Value.Length > 0)
        {
            await writer.WriteLineAsync("* BYE APPEND rejected with pending continuation data");
            session.State = ImapState.Logout;
            return false;
        }

        return true;
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

    private static (
        string? mailboxName,
        List<string> flags,
        DateTime? internalDate,
        int? literalSize,
        bool isLiteral8,
        bool isLiteralPlus) ParseAppendArgs(
        string args,
        bool utf8Enabled)
    {
        var tokens = ParseImapTokens(args);
        if (tokens.Count < 1)
            return (null, [], null, null, false, false);

        if (!TryParseMailboxName(tokens[0], utf8Enabled, out var mailboxName))
            return (null, [], null, null, false, false);
        var flags = new List<string>();
        DateTime? internalDate = null;
        int? literalSize = null;
        var isLiteral8 = false;
        var isLiteralPlus = false;

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
            else if ((token.StartsWith('{') || token.StartsWith("~{", StringComparison.Ordinal))
                && token.EndsWith('}'))
            {
                var literalValue = token[(token[0] == '~' ? 2 : 1)..^1];
                isLiteralPlus = literalValue.EndsWith('+');
                if (int.TryParse(literalValue.TrimEnd('+'), out var size))
                {
                    literalSize = size;
                    isLiteral8 = token[0] == '~';
                }
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
                var literalValue = args[(braceIdx + 1)..braceEnd];
                isLiteralPlus = literalValue.EndsWith('+');
                if (int.TryParse(literalValue.TrimEnd('+'), out var size))
                {
                    literalSize = size;
                    isLiteral8 = braceIdx > 0 && args[braceIdx - 1] == '~';
                }
            }
        }

        return (mailboxName, flags, internalDate, literalSize, isLiteral8, isLiteralPlus);
    }

    private async Task HandleIdleAsync(
        BoundedLineReader reader, StreamWriter writer, string tag,
        ImapSession session, CancellationTokenSource timeout, int connectionTimeoutSeconds)
    {
        await writer.WriteLineAsync("+ idling");

        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        var knownMessages = new List<EmailDB>();
        long lastKnownModSeq = 0;

        if (session.SelectedFolderId is not null)
        {
            using var initScope = scopeFactory.CreateScope();
            var initDb = initScope.ServiceProvider.GetRequiredService<EmailDbContext>();
            knownMessages = await GetEmailMetadataInFolderAsync(
                initDb,
                session.SelectedFolderId.Value,
                timeout.Token);
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

                    var currentMessages = await GetEmailMetadataInFolderAsync(
                        pollDb,
                        session.SelectedFolderId.Value,
                        timeout.Token);
                    var folder = await pollDb.Folders.AsNoTracking()
                        .FirstOrDefaultAsync(f => f.Id == session.SelectedFolderId.Value, timeout.Token);
                    var currentModSeq = folder?.HighestModSeq ?? 0;

                    var currentIds = currentMessages
                        .Select(email => email.Id)
                        .ToHashSet();
                    var removed = knownMessages
                        .Where(email => !currentIds.Contains(email.Id))
                        .ToList();
                    var survivingKnownMessages = knownMessages.ToList();
                    if (removed.Count > 0 && session.QresyncEnabled)
                    {
                        var vanishedUids = removed
                            .Select(email => email.Uid)
                            .Order()
                            .ToList();
                        await writer.WriteLineAsync($"* VANISHED {FormatUidRange(vanishedUids)}");
                        survivingKnownMessages.RemoveAll(email => !currentIds.Contains(email.Id));
                    }
                    else if (removed.Count > 0)
                    {
                        for (var index = 0; index < survivingKnownMessages.Count;)
                        {
                            if (currentIds.Contains(survivingKnownMessages[index].Id))
                            {
                                index++;
                                continue;
                            }

                            await writer.WriteLineAsync($"* {index + 1} EXPUNGE");
                            survivingKnownMessages.RemoveAt(index);
                        }
                    }

                    if (currentMessages.Count != survivingKnownMessages.Count)
                    {
                        await writer.WriteLineAsync($"* {currentMessages.Count} EXISTS");
                    }

                    if (currentModSeq > lastKnownModSeq)
                    {
                        var changed = currentMessages
                            .Where(email => email.ModSeq > lastKnownModSeq)
                            .ToList();

                        if (changed.Count > 0)
                        {
                            foreach (var email in changed)
                            {
                                var seqIdx = currentMessages.FindIndex(
                                    candidate => candidate.Id == email.Id);
                                if (seqIdx >= 0)
                                {
                                    var seqNum = seqIdx + 1;
                                    var flags = BuildFlagsList(email);
                                    var modSeq = session.CondstoreEnabled
                                        ? $" MODSEQ ({email.ModSeq})"
                                        : string.Empty;
                                    await writer.WriteLineAsync(
                                        $"* {seqNum} FETCH (FLAGS ({flags}){modSeq})");
                                }
                            }
                        }

                        lastKnownModSeq = currentModSeq;
                    }

                    knownMessages = currentMessages;
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
