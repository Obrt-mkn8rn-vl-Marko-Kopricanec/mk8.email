using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Configuration;
using mk8.email.Contracts.Enums;
using mk8.email.Contracts.Imap;
using mk8.email.Imap.Presentation.Protocol;
using mk8.email.MailWire;
using mk8.email.Messaging;

namespace mk8.email.Imap.Presentation;

public partial class ImapServerService(
IServiceScopeFactory scopeFactory,
EnvironmentConfig env,
ILogger<ImapServerService> logger,
IGatewayTrafficJournal? journal = null) : BackgroundService
{
    private const int MaximumCommandLineCharacters = 16 * 1024;
    private const int MaximumAuthenticationLineCharacters = 4096;
    private const int MaximumConcurrentConnections = 256;
    private const int MaximumConcurrentMessageWriteCommands = 2;
    private const int MaximumConcurrentFetchCommands = 4;
    private const int MaximumConcurrentSearchCommands = 4;
    private const int MaximumSearchTokens = 4096;
    private const int MaximumMultiAppendMessages = 20;
    private const int MaximumCommandLiterals = 64;
    private const int MaximumKeywordsPerMessage = ImapFlagSyntax.MaximumKeywordsPerMessage;
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

    private sealed class FetchPresentationMessage(ImapFetchMessage fetched)
    {
        public Guid Id { get; } = fetched.Id;
        public int Uid { get; } = fetched.Uid;
        public long ModSeq { get; set; } = fetched.ModSeq;
        public bool IsRead { get; set; } = fetched.IsRead;
        public bool IsDeleted { get; } = fetched.IsDeleted;
        public bool IsFlagged { get; } = fetched.IsFlagged;
        public bool IsDraft { get; } = fetched.IsDraft;
        public bool IsAnswered { get; } = fetched.IsAnswered;
        public string[] Keywords { get; } = fetched.Keywords;
        public DateTime ReceivedAt { get; } = fetched.ReceivedAt;
        public int SizeBytes { get; } = fetched.SizeBytes;
        public string Sender { get; } = fetched.Sender;
        public string Recipient { get; } = fetched.Recipient;
        public string? Cc { get; } = fetched.Cc;
        public string Subject { get; } = fetched.Subject;
        public string Body { get; set; } = fetched.Body;
        public string? RawHeaders { get; set; } = fetched.RawHeaders;
        public string? MessageId { get; } = fetched.MessageId;
        public string? InReplyTo { get; } = fetched.InReplyTo;
        public string? EmailObjectId { get; } = fetched.EmailObjectId;
        public string? ThreadObjectId { get; } = fetched.ThreadObjectId;
        public byte[]? RawMessage { get; set; }
    }

    private enum ImapState { NotAuthenticated, Authenticated, Selected, Logout }

    private enum SessionUpgrade { None, StartTls, Compress }

    private enum BinaryFetchKind { Content, Size }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct MessageSetRange(int Start, int End);
    private readonly record struct BinaryFetchRequest(
        BinaryFetchKind Kind,
        string Section,
        bool Peek,
        uint? Offset,
        uint? Count);
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
    private readonly NonBlockingCommandLimiter _messageWriteCommandLimiter = new(MaximumConcurrentMessageWriteCommands);
    private readonly NonBlockingCommandLimiter _fetchCommandLimiter = new(MaximumConcurrentFetchCommands);
    private readonly NonBlockingCommandLimiter _searchCommandLimiter = new(MaximumConcurrentSearchCommands);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = ImapListenerConfig.From(env);

        var tasks = new List<Task>();

        if (config.EnableImap)
            tasks.Add(ListenAsync(config.ImapPort, ListenerMode.Imap, config, stoppingToken));

        if (config.EnableImapImplicitTls)
            tasks.Add(ListenAsync(config.ImapImplicitTlsPort, ListenerMode.ImplicitTls, config, stoppingToken));

        if (tasks.Count == 0)
        {
            LogNoListeners(logger);
            return;
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ListenAsync(int port, ListenerMode mode, ImapListenerConfig config, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        LogListenerStarted(logger, mode, port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                // The detached handler owns its accepted client and observes shutdown cancellation.
#pragma warning disable CA2025
                _ = HandleConnectionAsync(client, mode, config, ct);
#pragma warning restore CA2025
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            LogListenerStopped(logger, mode, port);
        }
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleConnectionAsync(TcpClient client, ListenerMode mode, ImapListenerConfig config, CancellationToken ct)
#pragma warning restore MA0051
    {
        using var clientLifetime = client;
        var remoteEndpoint = client.Client.RemoteEndPoint;
        var remoteIp = (remoteEndpoint as IPEndPoint)?.Address ?? IPAddress.None;
        var remoteLabel = remoteEndpoint?.ToString() ?? "unknown";

        using var connectionLease = _connectionLimiter.TryAcquire(
            remoteIp,
            config.MaxConnectionsPerIp);
        if (connectionLease is null)
        {
            LogConnectionRejected(logger, remoteLabel);
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));

            Stream stream = client.GetStream();
            try
            {
                if (journal is not null)
                {
                    var traffic = new GatewayTrafficSession(
                        journal,
                        "imap",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["remoteEndpoint"] = remoteLabel,
                            ["listenerPort"] = ((client.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0)
                                .ToString(CultureInfo.InvariantCulture),
                        });
                    // The stream finally below owns the whole wrapper chain.
#pragma warning disable CA2000
                    stream = new GatewayTrafficStream(stream, traffic, leaveInnerOpen: false);
#pragma warning restore CA2000
                }
                if (mode == ListenerMode.ImplicitTls)
                {
                    if (config.TlsCertificatePath is null)
                        throw new InvalidOperationException("Implicit TLS requires a certificate.");

                    var upgraded = await TryUpgradeToTlsAsync(
                        stream, config, remoteLabel, timeout.Token).ConfigureAwait(false);
                    if (upgraded is null)
                        return;
                    stream = upgraded;
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
                    upgrade = await RunImapSessionAsync(
                        stream, config, session, timeout, sendGreeting).ConfigureAwait(false);
                    sendGreeting = false;

                    if (upgrade == SessionUpgrade.StartTls && config.TlsCertificatePath is not null)
                    {
                        var upgraded = await TryUpgradeToTlsAsync(
                            stream, config, remoteLabel, timeout.Token).ConfigureAwait(false);
                        if (upgraded is null)
                            return;
                        stream = upgraded;
                        session.IsSecure = true;
                    }
                    else if (upgrade == SessionUpgrade.Compress)
                    {
                        // The final active stream owns compression and the underlying transport.
#pragma warning disable CA2000
                        stream = CreateCompressedStream(stream);
#pragma warning restore CA2000
                    }
                } while (upgrade != SessionUpgrade.None && session.State != ImapState.Logout);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            LogConnectionTimedOut(logger, remoteLabel);
        }
        // The connection boundary must log and close any failed client task.
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogConnectionError(logger, ex, remoteLabel);
        }
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task<SessionUpgrade> RunImapSessionAsync(
#pragma warning restore MA0051
        Stream stream, ImapListenerConfig config, ImapSession session, CancellationTokenSource timeout, bool sendGreeting = true)
    {
        var ct = timeout.Token;

        using var streamReader = new StreamReader(stream, ProtocolEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var reader = new BoundedLineReader(streamReader);
        var writer = new StreamWriter(stream, ProtocolEncoding, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };
        await using (writer.ConfigureAwait(false))
        {
            if (sendGreeting)
                await writer.WriteLineAsync($"* OK {config.SmtpHostname} IMAP4rev1 mk8.email ready").ConfigureAwait(false);

            while (!timeout.IsCancellationRequested && session.State != ImapState.Logout)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds));
                var lineResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
                if (lineResult.IsTooLong)
                {
                    await writer.WriteLineAsync("* BYE Command line is too long").ConfigureAwait(false);
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
                    config.ConnectionTimeoutSeconds).ConfigureAwait(false);
                if (line is null)
                    continue;

                var spaceIdx = line.IndexOf(' ', StringComparison.Ordinal);
                if (spaceIdx <= 0)
                {
                    await writer.WriteLineAsync("* BAD Invalid command").ConfigureAwait(false);
                    continue;
                }

                var tag = line[..spaceIdx];
                var rest = line[(spaceIdx + 1)..];

                var cmdSpaceIdx = rest.IndexOf(' ', StringComparison.Ordinal);
                var command = (cmdSpaceIdx > 0 ? rest[..cmdSpaceIdx] : rest).ToUpperInvariant();
                var args = cmdSpaceIdx > 0 ? rest[(cmdSpaceIdx + 1)..] : string.Empty;

                switch (command)
                {
                    case "CAPABILITY":
                        await HandleCapabilityAsync(writer, tag, config, session).ConfigureAwait(false);
                        break;

                    case "NOOP":
                        await writer.WriteLineAsync($"{tag} OK NOOP completed").ConfigureAwait(false);
                        break;

                    case "LOGOUT":
                        await writer.WriteLineAsync("* BYE IMAP4rev1 server logging out").ConfigureAwait(false);
                        await writer.WriteLineAsync($"{tag} OK LOGOUT completed").ConfigureAwait(false);
                        session.State = ImapState.Logout;
                        break;

                    case "STARTTLS":
                        if (session.IsSecure)
                        {
                            await writer.WriteLineAsync($"{tag} BAD TLS is already active").ConfigureAwait(false);
                            break;
                        }
                        if (session.State != ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} BAD STARTTLS is only available before authentication").ConfigureAwait(false);
                            break;
                        }
                        if (config.EnableStartTls && config.TlsCertificatePath is not null)
                        {
                            await writer.WriteLineAsync($"{tag} OK Begin TLS negotiation").ConfigureAwait(false);
                            await writer.FlushAsync(ct).ConfigureAwait(false);
                            return SessionUpgrade.StartTls;
                        }
                        await writer.WriteLineAsync($"{tag} BAD STARTTLS not enabled").ConfigureAwait(false);
                        break;

                    case "LOGIN":
                        await HandleLoginAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        await StopAfterTooManyAuthenticationFailuresAsync(writer, session).ConfigureAwait(false);
                        break;

                    case "AUTHENTICATE":
                        await HandleAuthenticateAsync(reader, writer, tag, args, session, ct).ConfigureAwait(false);
                        await StopAfterTooManyAuthenticationFailuresAsync(writer, session).ConfigureAwait(false);
                        break;

                    case "NAMESPACE":
                        await writer.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL").ConfigureAwait(false);
                        await writer.WriteLineAsync($"{tag} OK NAMESPACE completed").ConfigureAwait(false);
                        break;

                    case "ID":
                        await writer.WriteLineAsync("* ID (\"name\" \"mk8.email\" \"version\" \"1.0\")").ConfigureAwait(false);
                        await writer.WriteLineAsync($"{tag} OK ID completed").ConfigureAwait(false);
                        break;

                    case "ENABLE":
                        if (session.State != ImapState.Authenticated)
                        {
                            await writer.WriteLineAsync($"{tag} BAD ENABLE is only valid in the authenticated state").ConfigureAwait(false);
                            break;
                        }
                        await HandleEnableAsync(writer, tag, args, session).ConfigureAwait(false);
                        break;

                    case "SUBSCRIBE":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleSubscribeAsync(writer, tag, args, session, subscribe: true, ct).ConfigureAwait(false);
                        break;

                    case "UNSUBSCRIBE":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleSubscribeAsync(writer, tag, args, session, subscribe: false, ct).ConfigureAwait(false);
                        break;

                    case "GETQUOTAROOT":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleGetQuotaRootAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "GETQUOTA":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleGetQuotaAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "LIST":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleListAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "LSUB":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleLsubAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "SELECT":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleSelectAsync(writer, tag, args, session, readOnly: false, ct).ConfigureAwait(false);
                        break;

                    case "EXAMINE":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleSelectAsync(writer, tag, args, session, readOnly: true, ct).ConfigureAwait(false);
                        break;

                    case "CREATE":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleCreateAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "DELETE":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleDeleteAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "RENAME":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleRenameAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "STATUS":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleStatusAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "FETCH":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleFetchAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "STORE":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleStoreAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "SEARCH":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleSearchAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "EXPUNGE":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        if (session.SelectedReadOnly)
                        {
                            await writer.WriteLineAsync($"{tag} NO Mailbox is read-only").ConfigureAwait(false);
                            break;
                        }
                        await HandleExpungeAsync(writer, tag, session, ct).ConfigureAwait(false);
                        break;

                    case "COPY":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleCopyAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "MOVE":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        if (session.SelectedReadOnly)
                        {
                            await writer.WriteLineAsync($"{tag} NO Mailbox is read-only").ConfigureAwait(false);
                            break;
                        }
                        await HandleMoveAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "APPEND":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
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
                            config.ConnectionTimeoutSeconds).ConfigureAwait(false);
                        break;

                    case "IDLE":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        await HandleIdleAsync(reader, writer, tag, session, timeout, config.ConnectionTimeoutSeconds).ConfigureAwait(false);
                        break;

                    case "CHECK":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await writer.WriteLineAsync($"{tag} OK CHECK completed").ConfigureAwait(false);
                        break;

                    case "CLOSE":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        if (!session.SelectedReadOnly)
                        {
                            var closeResult = await TryExpungeDeletedAsync(
                                writer, tag, "CLOSE", session, ct).ConfigureAwait(false);
                            if (closeResult is null)
                                break;
                            if (!closeResult.FolderFound)
                            {
                                await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
                                break;
                            }
                        }
                        session.SelectedFolderId = null;
                        session.SelectedFolderName = null;
                        session.State = ImapState.Authenticated;
                        await writer.WriteLineAsync($"{tag} OK CLOSE completed").ConfigureAwait(false);
                        break;

                    case "UNSELECT":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        session.SelectedFolderId = null;
                        session.SelectedFolderName = null;
                        session.State = ImapState.Authenticated;
                        await writer.WriteLineAsync($"{tag} OK UNSELECT completed").ConfigureAwait(false);
                        break;

                    case "UID":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleUidAsync(writer, tag, args, session, ct).ConfigureAwait(false);
                        break;

                    case "SORT":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleSortAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);
                        break;

                    case "THREAD":
                        if (session.State != ImapState.Selected)
                        {
                            await writer.WriteLineAsync($"{tag} NO No mailbox selected").ConfigureAwait(false);
                            break;
                        }
                        await HandleThreadAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);
                        break;

                    case "COMPRESS":
                        if (session.State == ImapState.NotAuthenticated)
                        {
                            await writer.WriteLineAsync($"{tag} NO Not authenticated").ConfigureAwait(false);
                            break;
                        }
                        if (session.CompressEnabled)
                        {
                            await writer.WriteLineAsync($"{tag} BAD COMPRESS already active").ConfigureAwait(false);
                            break;
                        }
                        if (!await HandleCompressAsync(writer, tag, args).ConfigureAwait(false))
                            break;
                        session.CompressEnabled = true;
                        return SessionUpgrade.Compress;

                    default:
                        await writer.WriteLineAsync($"{tag} BAD Command not recognized").ConfigureAwait(false);
                        break;
                }
            }

            return SessionUpgrade.None;
        }
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static async Task<string?> ReadCommandLiteralsAsync(
#pragma warning restore MA0051
        string initialLine,
        BoundedLineReader reader,
        StreamWriter writer,
        ImapSession session,
        CancellationTokenSource timeout,
        int connectionTimeoutSeconds)
    {
        var line = initialLine;
        var tagEnd = line.IndexOf(' ', StringComparison.Ordinal);
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
                    await writer.WriteLineAsync("* BYE [PRIVACYREQUIRED] TLS is required for authentication").ConfigureAwait(false);
                    session.State = ImapState.Logout;
                }
                else
                {
                    await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] TLS is required for authentication").ConfigureAwait(false);
                }
                return null;
            }

            if (literalIndex >= MaximumCommandLiterals
                || !int.TryParse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out var literalSize)
                || literalSize > MaximumCommandLineCharacters)
            {
                await RejectCommandLiteralAsync(
                    writer,
                    tag,
                    session,
                    isNonSynchronizing,
                    "Command literal exceeds the server limit").ConfigureAwait(false);
                return null;
            }

            if (!isNonSynchronizing)
                await writer.WriteLineAsync("+ Ready for literal data").ConfigureAwait(false);

            var literal = new char[literalSize];
            var totalRead = 0;
            while (totalRead < literal.Length)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                var read = await reader.ReadAsync(
                    literal.AsMemory(totalRead, literal.Length - totalRead),
                    timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    session.State = ImapState.Logout;
                    return null;
                }
                totalRead += read;
            }

            if (literal.AsSpan().Contains('\0'))
            {
                await writer.WriteLineAsync("* BYE Binary command literals are not supported").ConfigureAwait(false);
                session.State = ImapState.Logout;
                return null;
            }

            timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
            var remainderResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, timeout.Token).ConfigureAwait(false);
            if (remainderResult.IsTooLong)
            {
                await writer.WriteLineAsync("* BYE Command continuation is too long").ConfigureAwait(false);
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
                await writer.WriteLineAsync("* BYE Expanded command exceeds the server limit").ConfigureAwait(false);
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
        var tagEnd = line.IndexOf(' ', StringComparison.Ordinal);
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
            await writer.WriteLineAsync($"* BYE {response}").ConfigureAwait(false);
            session.State = ImapState.Logout;
            return;
        }

        await writer.WriteLineAsync($"{tag} BAD {response}").ConfigureAwait(false);
    }

    private static async Task<bool> HandleCompressAsync(StreamWriter writer, string tag, string args)
    {
        var mechanism = args.Trim().ToUpperInvariant();
        if (!string.Equals(mechanism, "DEFLATE", StringComparison.Ordinal))
        {
            await writer.WriteLineAsync($"{tag} BAD Unknown compression mechanism").ConfigureAwait(false);
            return false;
        }

        // Signal OK — the caller will upgrade the stream
        await writer.WriteLineAsync($"{tag} OK COMPRESS DEFLATE active").ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        return true;
    }

    private async Task HandleCapabilityAsync(
        StreamWriter writer,
        string tag,
        ImapListenerConfig config,
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
        await writer.WriteLineAsync($"* CAPABILITY {caps}").ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK CAPABILITY completed").ConfigureAwait(false);
    }

    private async Task HandleLoginAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] TLS is required for authentication").ConfigureAwait(false);
            return;
        }

        if (session.State != ImapState.NotAuthenticated)
        {
            await writer.WriteLineAsync($"{tag} BAD Already authenticated").ConfigureAwait(false);
            return;
        }

        var (username, password) = ParseLoginArgs(args);
        if (username is null || password is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error in LOGIN").ConfigureAwait(false);
            return;
        }

        ImapIdentityResult user;
        try
        {
            user = await AuthenticateUserAsync(username, password, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogPasswordAuthenticationUnavailable(logger, exception);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Authentication service unavailable").ConfigureAwait(false);
            return;
        }
        if (user.UserId is null || user.Username is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO LOGIN failed").ConfigureAwait(false);
            return;
        }

        session.UserId = user.UserId.Value;
        session.UserName = user.Username;
        session.State = ImapState.Authenticated;
        await writer.WriteLineAsync($"{tag} OK LOGIN completed").ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleAuthenticateAsync(
#pragma warning restore MA0051
        BoundedLineReader reader, StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!session.IsSecure)
        {
            await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] TLS is required for authentication").ConfigureAwait(false);
            return;
        }

        if (session.State != ImapState.NotAuthenticated)
        {
            await writer.WriteLineAsync($"{tag} BAD Already authenticated").ConfigureAwait(false);
            return;
        }

        var authenticationArgs = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (authenticationArgs.Length == 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Missing authentication mechanism").ConfigureAwait(false);
            return;
        }

        var mechanism = authenticationArgs[0].ToUpperInvariant();

        if (mechanism is not ("PLAIN" or "XOAUTH2")
            || string.Equals(mechanism, "XOAUTH2", StringComparison.Ordinal) && !env.OAuth.EnableOAuth)
        {
            await writer.WriteLineAsync($"{tag} NO Unsupported authentication mechanism").ConfigureAwait(false);
            return;
        }

        string? encoded;
        if (authenticationArgs.Length == 2)
        {
            encoded = authenticationArgs[1];
            if (encoded.Length > MaximumAuthenticationLineCharacters)
            {
                await writer.WriteLineAsync($"{tag} BAD Authentication response is too long").ConfigureAwait(false);
                return;
            }

            if (string.Equals(encoded, "=", StringComparison.Ordinal))
                encoded = string.Empty;
        }
        else
        {
            await writer.WriteLineAsync("+ ").ConfigureAwait(false);
            var encodedResult = await reader.ReadLineAsync(MaximumAuthenticationLineCharacters, ct).ConfigureAwait(false);
            encoded = encodedResult.Value;
            if (encodedResult.IsTooLong)
            {
                await writer.WriteLineAsync($"{tag} BAD Authentication response is too long").ConfigureAwait(false);
                return;
            }
        }

        if (encoded is null || string.Equals(encoded, "*", StringComparison.Ordinal))
        {
            await writer.WriteLineAsync($"{tag} BAD Authentication cancelled").ConfigureAwait(false);
            return;
        }

        if (string.Equals(mechanism, "XOAUTH2", StringComparison.Ordinal))
        {
            if (!OAuthSasl.TryParseXOAuth2(encoded, out var oauthUsername, out var accessToken))
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync($"{tag} NO Authentication failed").ConfigureAwait(false);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            ImapIdentityResult oauthUser;
            try
            {
                oauthUser = await application.AuthenticateOAuthAsync(
                    new ImapOAuthAuthentication(oauthUsername, accessToken), ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                LogOAuthAuthenticationUnavailable(logger, exception);
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Authentication service unavailable").ConfigureAwait(false);
                return;
            }
            if (oauthUser.UserId is null || oauthUser.Username is null)
            {
                RecordAuthenticationFailure(session);
                await writer.WriteLineAsync($"{tag} NO Authentication failed").ConfigureAwait(false);
                return;
            }

            session.UserId = oauthUser.UserId.Value;
            session.UserName = oauthUser.Username;
            session.State = ImapState.Authenticated;
            await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed").ConfigureAwait(false);
            return;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(encoded));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid base64").ConfigureAwait(false);
            return;
        }

        var firstSeparator = decoded.IndexOf('\0', StringComparison.Ordinal);
        var secondSeparator = firstSeparator < 0
            ? -1
            : decoded.IndexOf('\0', firstSeparator + 1);
        if (firstSeparator < 0 || secondSeparator < 0)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO Authentication failed").ConfigureAwait(false);
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
            await writer.WriteLineAsync($"{tag} NO Authentication failed").ConfigureAwait(false);
            return;
        }

        ImapIdentityResult user;
        try
        {
            user = await AuthenticateUserAsync(username, password, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogSaslAuthenticationUnavailable(logger, exception);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Authentication service unavailable").ConfigureAwait(false);
            return;
        }
        if (user.UserId is null || user.Username is null)
        {
            RecordAuthenticationFailure(session);
            await writer.WriteLineAsync($"{tag} NO Authentication failed").ConfigureAwait(false);
            return;
        }

        session.UserId = user.UserId.Value;
        session.UserName = user.Username;
        session.State = ImapState.Authenticated;
        await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed").ConfigureAwait(false);
    }

    private async Task<ImapIdentityResult> AuthenticateUserAsync(
        string username,
        string password,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
        return await application.AuthenticatePasswordAsync(
            new ImapPasswordAuthentication(username, password), ct).ConfigureAwait(false);
    }

    private void RecordAuthenticationFailure(ImapSession session)
    {
        session.AuthenticationFailures++;
        LogAuthenticationFailure(logger, session.RemoteIp);
    }

    private static async Task StopAfterTooManyAuthenticationFailuresAsync(
        StreamWriter writer,
        ImapSession session)
    {
        if (session.AuthenticationFailures < 5)
            return;

        await writer.WriteLineAsync("* BYE Too many authentication failures").ConfigureAwait(false);
        session.State = ImapState.Logout;
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleListAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
#pragma warning restore MA0051
    {
        if (!TryParseListCommand(args, session.Utf8Enabled, out var options, out var failureResponse))
        {
            await writer.WriteLineAsync($"{tag} BAD {failureResponse}").ConfigureAwait(false);
            return;
        }

        if (!options.IsExtended
            && options.Patterns.Count == 1
            && string.IsNullOrEmpty(options.Patterns[0]))
        {
            await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"").ConfigureAwait(false);
            await writer.WriteLineAsync($"{tag} OK LIST completed").ConfigureAwait(false);
            return;
        }

        List<MailboxFolderInfo> folders;
        try
        {
            folders = await GetUserFoldersAsync(session.UserId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogMailboxListingUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailboxes unavailable").ConfigureAwait(false);
            return;
        }
        var entries = BuildMailboxListEntries(folders);
        Dictionary<string, string> statusLines = new(StringComparer.Ordinal);
        if (options.StatusItems.Length > 0)
        {
            try
            {
                statusLines = await GetMailboxStatusLinesAsync(
                    session.UserId,
                    entries
                        .Where(entry => entry.IsSelectable
                            && MatchesAnyPattern(entry.FullName, options.Reference, options.Patterns))
                        .Select(entry => entry.FullName)
                        .ToList(),
                    options.StatusItems,
                    ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                LogListStatusUnavailable(logger, exception, session.UserId);
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox status unavailable").ConfigureAwait(false);
                return;
            }
        }

        // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
        foreach (var entry in entries)
#pragma warning restore HLQ012
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
                $"* LIST ({attrs}) \"/\" \"{EscapeImapString(FormatWireMailboxName(entry.FullName, session.Utf8Enabled))}\"{childInfo}").ConfigureAwait(false);

            if (entry.IsSelectable
                && matchesSelection
                && statusLines.TryGetValue(entry.FullName, out var statusResult))
            {
                await writer.WriteLineAsync(
                    $"* STATUS \"{EscapeImapString(FormatWireMailboxName(entry.FullName, session.Utf8Enabled))}\" ({statusResult})").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync($"{tag} OK LIST completed").ConfigureAwait(false);
    }

    private async Task HandleLsubAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!TryParseMailboxArgs(args, session.Utf8Enabled, out var reference, out var pattern))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }

        List<MailboxFolderInfo> folders;
        try
        {
            folders = await GetUserFoldersAsync(session.UserId, ct, subscribedOnly: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogSubscribedListingUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailboxes unavailable").ConfigureAwait(false);
            return;
        }

        // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
        foreach (var entry in BuildMailboxListEntries(folders))
#pragma warning restore HLQ012
        {
            if (MatchesPattern(entry.FullName, reference, pattern))
            {
                var attrs = GetFolderAttributes(
                    entry.FolderName,
                    entry.IsSelectable,
                    entry.HasChildren);
                await writer.WriteLineAsync(
                    $"* LSUB ({attrs}) \"/\" \"{EscapeImapString(FormatWireMailboxName(entry.FullName, session.Utf8Enabled))}\"").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync($"{tag} OK LSUB completed").ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleSelectAsync(
#pragma warning restore MA0051
        StreamWriter writer, string tag, string args, ImapSession session, bool readOnly, CancellationToken ct)
    {
        var selectArgs = args.Trim();
        var parenIdx = selectArgs.IndexOf('(', StringComparison.Ordinal);
        int? qresyncUidValidity = null;
        long? qresyncModSeq = null;
        if (parenIdx >= 0)
        {
            var modifiers = selectArgs[parenIdx..].ToUpperInvariant();
            if (modifiers.Contains("CONDSTORE", StringComparison.Ordinal))
                session.CondstoreEnabled = true;

            if (modifiers.Contains("QRESYNC", StringComparison.Ordinal) && session.QresyncEnabled)
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
                            if (int.TryParse(qrParams[0], System.Globalization.CultureInfo.InvariantCulture, out var uv)) qresyncUidValidity = uv;
                            if (long.TryParse(qrParams[1], System.Globalization.CultureInfo.InvariantCulture, out var ms)) qresyncModSeq = ms;
                        }
                    }
                }
            }

            selectArgs = selectArgs[..parenIdx].Trim();
        }

        if (!TryParseMailboxName(selectArgs, session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }

        ImapSelectedMailbox? mailbox;
        List<string>? responseLines = null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            var result = await application.SelectMailboxAsync(
                new ImapMailboxSelectRequest(
                    session.UserId, mailboxName, qresyncUidValidity, qresyncModSeq), ct).ConfigureAwait(false);
            mailbox = result.Mailbox;
            if (mailbox is not null)
                responseLines = BuildMailboxSelectionLines(mailbox);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogMailboxSelectionUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox selection unavailable").ConfigureAwait(false);
            return;
        }

        if (mailbox is null)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }
        session.SelectedFolderId = mailbox.FolderId;
        session.SelectedFolderName = mailboxName;
        session.SelectedReadOnly = readOnly;
        session.SavedSearchUids = [];
        session.State = ImapState.Selected;
        // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
        foreach (var line in responseLines!)
#pragma warning restore HLQ012
            await writer.WriteLineAsync(line).ConfigureAwait(false);

        var cmdName = readOnly ? "EXAMINE" : "SELECT";
        var access = readOnly ? "[READ-ONLY]" : "[READ-WRITE]";
        await writer.WriteLineAsync($"{tag} OK {access} {cmdName} completed").ConfigureAwait(false);
    }

    private static List<string> BuildMailboxSelectionLines(ImapSelectedMailbox mailbox)
    {
        if (mailbox.FolderId == Guid.Empty
            || string.IsNullOrEmpty(mailbox.MailboxId)
            || mailbox.MessageCount < 0
            || mailbox.Keywords is null
            || mailbox.VanishedUids is null
            || mailbox.ChangedMessages is null
            || mailbox.FirstUnseenSequence is < 1
            || mailbox.FirstUnseenSequence > mailbox.MessageCount)
        {
            throw new InvalidOperationException("The IMAP mailbox selection result is invalid.");
        }

        var keywords = mailbox.Keywords
            .Where(IsValidImapKeyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var definedFlags = keywords.Length == 0
            ? "\\Seen \\Answered \\Flagged \\Deleted \\Draft"
            : $"\\Seen \\Answered \\Flagged \\Deleted \\Draft {string.Join(' ', keywords)}";
        var lines = new List<string>
        {
            $"* {mailbox.MessageCount} EXISTS",
            "* 0 RECENT",
            $"* FLAGS ({definedFlags})",
            "* OK [PERMANENTFLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft \\*)] Flags permitted",
            $"* OK [UIDVALIDITY {mailbox.UidValidity}]",
            $"* OK [UIDNEXT {mailbox.NextUid}]",
            $"* OK [HIGHESTMODSEQ {mailbox.HighestModSeq}]",
            $"* OK [MAILBOXID ({FormatObjectId('F', mailbox.MailboxId, mailbox.FolderId)})] Mailbox identifier",
        };
        if (mailbox.FirstUnseenSequence is not null)
            lines.Add($"* OK [UNSEEN {mailbox.FirstUnseenSequence}]");
        if (mailbox.VanishedUids.Count > 0)
            lines.Add($"* VANISHED (EARLIER) {FormatUidRange(mailbox.VanishedUids)}");
        foreach (ref readonly var changed in CollectionsMarshal.AsSpan(mailbox.ChangedMessages))
        {
            if (changed.Sequence is < 1 || changed.Sequence > mailbox.MessageCount
                || changed.Uid < 1 || changed.Keywords is null)
            {
                throw new InvalidOperationException("The IMAP changed-message result is invalid.");
            }
            lines.Add($"* {changed.Sequence} FETCH (UID {changed.Uid} FLAGS ({BuildFlagsList(changed)}) MODSEQ ({changed.ModSeq}))");
        }
        return lines;
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
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }

        ImapMailboxCreateResult result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            result = await application.CreateMailboxAsync(
                new ImapMailboxCreateRequest(session.UserId, mailboxName), ct).ConfigureAwait(false);
            if (result.Disposition == ImapMailboxCreateDisposition.Created
                && (result.FolderId == Guid.Empty || string.IsNullOrEmpty(result.MailboxId)))
            {
                throw new InvalidOperationException("The IMAP mailbox creation result is incomplete.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogMailboxCreationUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox creation unavailable").ConfigureAwait(false);
            return;
        }

        switch (result.Disposition)
        {
            case ImapMailboxCreateDisposition.InvalidName:
                await writer.WriteLineAsync($"{tag} NO [CANNOT] Invalid mailbox name").ConfigureAwait(false);
                return;
            case ImapMailboxCreateDisposition.AlreadyExists:
                await writer.WriteLineAsync($"{tag} NO [ALREADYEXISTS] Mailbox already exists").ConfigureAwait(false);
                return;
            case ImapMailboxCreateDisposition.Created:
                await writer.WriteLineAsync(
                    $"{tag} OK [MAILBOXID ({FormatObjectId('F', result.MailboxId!, result.FolderId)})] CREATE completed").ConfigureAwait(false);
                return;
            default:
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox creation unavailable").ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleDeleteAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!TryParseMailboxName(args.Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }

        ImapMailboxDeleteResult result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            result = await application.DeleteMailboxAsync(
                new ImapMailboxDeleteRequest(session.UserId, mailboxName), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogMailboxDeletionUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox deletion unavailable").ConfigureAwait(false);
            return;
        }

        switch (result.Disposition)
        {
            case ImapMailboxDeleteDisposition.NotFound:
                await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Mailbox not found").ConfigureAwait(false);
                return;
            case ImapMailboxDeleteDisposition.SystemFolder:
                await writer.WriteLineAsync($"{tag} NO [CANNOT] System mailboxes cannot be deleted").ConfigureAwait(false);
                return;
            case ImapMailboxDeleteDisposition.Deleted:
                if (result.FolderId == Guid.Empty)
                {
                    await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox deletion unavailable").ConfigureAwait(false);
                    return;
                }
                break;
            default:
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox deletion unavailable").ConfigureAwait(false);
                return;
        }

        if (session.SelectedFolderId == result.FolderId)
        {
            session.SelectedFolderId = null;
            session.SelectedFolderName = null;
            session.State = ImapState.Authenticated;
        }

        await writer.WriteLineAsync($"{tag} OK DELETE completed").ConfigureAwait(false);
    }

    private async Task HandleRenameAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var parsedArgs = ParseTwoMailboxArgs(args, session.Utf8Enabled);
        if (parsedArgs is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        var (oldName, newName) = parsedArgs.Value;
        ImapMailboxRenameResult result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            result = await application.RenameMailboxAsync(
                new ImapMailboxRenameRequest(session.UserId, oldName, newName), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogMailboxRenameUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox rename unavailable").ConfigureAwait(false);
            return;
        }

        switch (result.Disposition)
        {
            case ImapMailboxRenameDisposition.NotFound:
                await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Mailbox not found").ConfigureAwait(false);
                return;
            case ImapMailboxRenameDisposition.SystemFolder:
                await writer.WriteLineAsync($"{tag} NO [CANNOT] System mailboxes cannot be renamed").ConfigureAwait(false);
                return;
            case ImapMailboxRenameDisposition.InvalidDestination:
                await writer.WriteLineAsync($"{tag} NO [CANNOT] Invalid rename destination").ConfigureAwait(false);
                return;
            case ImapMailboxRenameDisposition.AlreadyExists:
                await writer.WriteLineAsync($"{tag} NO [ALREADYEXISTS] Rename destination already exists").ConfigureAwait(false);
                return;
            case ImapMailboxRenameDisposition.Renamed:
                break;
            default:
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox rename unavailable").ConfigureAwait(false);
                return;
        }

        if (session.SelectedFolderName is not null
            && (string.Equals(session.SelectedFolderName, oldName, StringComparison.OrdinalIgnoreCase)
                || session.SelectedFolderName.StartsWith(oldName + "/", StringComparison.OrdinalIgnoreCase)))
        {
            session.SelectedFolderName = newName + session.SelectedFolderName[oldName.Length..];
        }

        await writer.WriteLineAsync($"{tag} OK RENAME completed").ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var parenIdx = args.IndexOf('(', StringComparison.Ordinal);
        if (parenIdx < 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        if (!TryParseMailboxName(args[..parenIdx].Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }
        var statusItemsRaw = args[(parenIdx + 1)..].TrimEnd(')').Trim();
        var statusItems = statusItemsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string> statusLines;
        try
        {
            statusLines = await GetMailboxStatusLinesAsync(
                session.UserId, [mailboxName], statusItems, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogStatusUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox status unavailable").ConfigureAwait(false);
            return;
        }
        if (!statusLines.TryGetValue(mailboxName, out var statusResult))
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }

        await writer.WriteLineAsync(
            $"* STATUS \"{EscapeImapString(FormatWireMailboxName(mailboxName, session.Utf8Enabled))}\" ({statusResult})").ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK STATUS completed").ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> GetMailboxStatusLinesAsync(
        Guid userId,
        List<string> mailboxNames,
        string[] statusItems,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
        var result = await application.GetMailboxStatusesAsync(
            new ImapMailboxStatusRequest(
                userId,
                mailboxNames,
                statusItems.Contains("MESSAGES", StringComparer.OrdinalIgnoreCase),
                statusItems.Contains("UNSEEN", StringComparer.OrdinalIgnoreCase),
                statusItems.Contains("SIZE", StringComparer.OrdinalIgnoreCase)),
            cancellationToken).ConfigureAwait(false);
        return result.Statuses.ToDictionary(
            entry => entry.Key,
            entry => BuildStatusResult(entry.Value, statusItems),
            StringComparer.Ordinal);
    }

    private static string BuildStatusResult(
        ImapMailboxStatus status,
        string[] statusItems)
    {
        var results = new StringBuilder();
        foreach (var item in statusItems)
        {
            if (results.Length > 0) results.Append(' ');
            switch (item.ToUpperInvariant())
            {
                case "MESSAGES":
                    results.Append(System.Globalization.CultureInfo.InvariantCulture, $"MESSAGES {status.MessageCount ?? throw new InvalidOperationException("Missing message count.")}");
                    break;
                case "RECENT":
                    results.Append("RECENT 0");
                    break;
                case "UNSEEN":
                    results.Append(System.Globalization.CultureInfo.InvariantCulture, $"UNSEEN {status.UnseenCount ?? throw new InvalidOperationException("Missing unseen count.")}");
                    break;
                case "UIDVALIDITY":
                    results.Append(System.Globalization.CultureInfo.InvariantCulture, $"UIDVALIDITY {status.UidValidity}");
                    break;
                case "UIDNEXT":
                    results.Append(System.Globalization.CultureInfo.InvariantCulture, $"UIDNEXT {status.NextUid}");
                    break;
                case "HIGHESTMODSEQ":
                    results.Append(System.Globalization.CultureInfo.InvariantCulture, $"HIGHESTMODSEQ {status.HighestModSeq}");
                    break;
                case "SIZE":
                    results.Append(System.Globalization.CultureInfo.InvariantCulture, $"SIZE {status.SizeBytes ?? throw new InvalidOperationException("Missing mailbox size.")}");
                    break;
                case "MAILBOXID":
                    results.Append(CultureInfo.InvariantCulture, $"MAILBOXID ({FormatObjectId('F', status.MailboxId, status.FolderId)})");
                    break;
            }
        }

        return results.ToString();
    }

    private async Task HandleFetchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleFetchWithLimitAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);
    }

    private async Task HandleFetchWithLimitAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        using var slot = _fetchCommandLimiter.TryAcquire();
        if (slot is null)
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent FETCH commands").ConfigureAwait(false);
            return;
        }

        await HandleFetchCoreAsync(writer, tag, args, session, useUid, ct).ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleFetchCoreAsync(
#pragma warning restore MA0051
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        var messageSet = args[..spaceIdx];
        var fetchItems = StripFetchList(args[(spaceIdx + 1)..]);
        if (!TryParseBinaryFetchRequests(fetchItems, out var binaryRequests))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid BINARY data item").ConfigureAwait(false);
            return;
        }

        var implicitSeen = ShouldSetSeen(fetchItems, binaryRequests);

        if (fetchItems.Contains("MODSEQ", StringComparison.OrdinalIgnoreCase))
            session.CondstoreEnabled = true;

        var folderId = session.SelectedFolderId!.Value;
        ImapMessageSelection selection;
        if (string.Equals(messageSet, "$", StringComparison.Ordinal))
        {
            selection = new ImapMessageSelection(null, session.SavedSearchUids.ToList());
        }
        else if (ImapUidSetParser.TryParse(messageSet, out var ranges))
        {
            selection = new ImapMessageSelection(
                ranges.Select(range => new ImapMessageRange(range.Start, range.End)).ToList(),
                null);
        }
        else
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set").ConfigureAwait(false);
            return;
        }

        var includeStoredContent = FetchNeedsStoredContent(fetchItems, binaryRequests);
        var normalizedFetchItems = fetchItems
            .ToUpperInvariant()
            .Replace("BODY.PEEK[", "BODY[", StringComparison.Ordinal);
        var needsMimeProjection = normalizedFetchItems.Contains("BODYSTRUCTURE", StringComparison.Ordinal)
            || BodyStandaloneRegex().IsMatch(normalizedFetchItems)
            || NumericBodySectionRegex().IsMatch(normalizedFetchItems)
            || binaryRequests.Count > 0;
        using var scope = scopeFactory.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
        var afterUid = 0;
        int? snapshotMaxUid = null;
        int? snapshotMaximumIdentifier = null;
        int? snapshotMessageCount = null;
        while (true)
        {
            ImapFetchPageResult page;
            try
            {
                page = await application.GetFetchPageAsync(new ImapFetchPageRequest(
                    session.UserId,
                    folderId,
                    useUid,
                    selection,
                    afterUid,
                    snapshotMaxUid,
                    snapshotMaximumIdentifier,
                    snapshotMessageCount,
                    includeStoredContent), ct).ConfigureAwait(false);
                if (!IsValidFetchPage(page, afterUid, snapshotMaxUid,
                    snapshotMaximumIdentifier, snapshotMessageCount, includeStoredContent))
                {
                    throw new InvalidOperationException("The IMAP FETCH page is invalid.");
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                LogFetchUnavailable(logger, exception, session.UserId);
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] FETCH backend unavailable").ConfigureAwait(false);
                return;
            }
            if (!page.FolderFound)
            {
                await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
                return;
            }

            // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
            foreach (var fetched in page.Messages)
#pragma warning restore HLQ012
            {
                var email = ToTransientFetchMessage(fetched);
                if (fetched.RawMessage is not null)
                    ApplyTransientRawMessage(email, fetched.RawMessage);

                // Conditional MIME projection is owned by this using declaration.
#pragma warning disable CA2000
                using var mimeMessage = needsMimeProjection
                    ? ImapMimeMessage.TryParse(BuildRfc822(email))
                    : null;
#pragma warning restore CA2000

                if (!TryDecodeBinarySections(
                        mimeMessage,
                        binaryRequests,
                        out var binarySections,
                        out var binaryFailure))
                {
                    await writer.WriteLineAsync($"{tag} NO {binaryFailure}").ConfigureAwait(false);
                    return;
                }

                if (implicitSeen && !email.IsRead && !session.SelectedReadOnly)
                {
                    ImapMarkSeenResult seenResult;
                    try
                    {
                        seenResult = await application.MarkMessagesSeenAsync(new ImapMarkSeenRequest(
                            session.UserId, folderId, [email.Id]), ct).ConfigureAwait(false);
                        if (seenResult is null
                            || seenResult.Messages is null
                            || seenResult.FolderFound && seenResult.Messages.Count != 1
                            || !seenResult.FolderFound && seenResult.Messages.Count != 0
                            || seenResult.Messages.Any(message => message is null
                                || message.Id != email.Id
                                || message.Found && message.ModSeq < 0
                                || !message.Found && message.ModSeq != 0))
                        {
                            throw new InvalidOperationException("The IMAP seen update is invalid.");
                        }
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        LogFetchSeenUnavailable(logger, exception, session.UserId);
                        await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] FETCH backend unavailable").ConfigureAwait(false);
                        return;
                    }
                    if (!seenResult.FolderFound || !seenResult.Messages[0].Found)
                    {
                        await writer.WriteLineAsync($"{tag} NO Message unavailable").ConfigureAwait(false);
                        return;
                    }
                    email.IsRead = true;
                    email.ModSeq = seenResult.Messages[0].ModSeq;
                }

                var response = BuildFetchResponse(
                    fetched.SequenceNumber,
                    email,
                    fetchItems,
                    useUid,
                    mimeMessage,
                    binaryRequests,
                    binarySections);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }

            if (!page.HasMore)
                break;
            afterUid = page.NextAfterUid;
            snapshotMaxUid = page.SnapshotMaxUid;
            snapshotMaximumIdentifier = page.SnapshotMaximumIdentifier;
            snapshotMessageCount = page.SnapshotMessageCount;
        }

        var commandName = useUid ? "UID FETCH" : "FETCH";
        await writer.WriteLineAsync($"{tag} OK {commandName} completed").ConfigureAwait(false);
    }

    private static bool IsValidFetchPage(
        ImapFetchPageResult? page,
        int afterUid,
        int? snapshotMaxUid,
        int? snapshotMaximumIdentifier,
        int? snapshotMessageCount,
        bool includeStoredContent)
    {
        if (page is null || page.Messages is null)
            return false;
        if (!page.FolderFound)
        {
            return page.SnapshotMaxUid == 0
                && page.SnapshotMaximumIdentifier == 0
                && page.SnapshotMessageCount == 0
                && page.NextAfterUid == afterUid
                && !page.HasMore
                && page.Messages.Count == 0;
        }
        if (page.SnapshotMaxUid < 0
            || page.SnapshotMaximumIdentifier < 0
            || page.SnapshotMessageCount < 0
            || page.SnapshotMessageCount > page.SnapshotMaxUid
            || page.NextAfterUid < afterUid
            || page.NextAfterUid > page.SnapshotMaxUid
            || page.HasMore && page.NextAfterUid == afterUid
            || snapshotMaxUid is not null && page.SnapshotMaxUid != snapshotMaxUid
            || snapshotMaximumIdentifier is not null
                && page.SnapshotMaximumIdentifier != snapshotMaximumIdentifier
            || snapshotMessageCount is not null
                && page.SnapshotMessageCount != snapshotMessageCount
            || page.Messages.Count > (includeStoredContent ? 8 : 128))
        {
            return false;
        }

        var previousUid = afterUid;
        var previousSequence = 0;
        foreach (ref readonly var message in CollectionsMarshal.AsSpan(page.Messages))
        {
            if (message is null
                || message.Id == Guid.Empty
                || message.Uid <= previousUid
                || message.Uid > page.NextAfterUid
                || message.SequenceNumber <= previousSequence
                || message.SequenceNumber > page.SnapshotMessageCount
                || message.ModSeq < 0
                || message.SizeBytes < 0
                || message.Sender is null
                || message.Recipient is null
                || message.Subject is null
                || message.Body is null
                || message.Keywords is null
                || message.Keywords.Any(keyword => keyword is null)
                || (message.RawMessage is not null) != includeStoredContent)
            {
                return false;
            }
            previousUid = message.Uid;
            previousSequence = message.SequenceNumber;
        }
        return true;
    }

    private static FetchPresentationMessage ToTransientFetchMessage(
        ImapFetchMessage fetched) => new(fetched);

    private async Task HandleStoreAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
        => await HandleStoreCoreAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleStoreCoreAsync(
#pragma warning restore MA0051
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        if (session.SelectedReadOnly)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox is read-only").ConfigureAwait(false);
            return;
        }

        var (messageSet, unchangedSince, action, flagsRaw) = ParseStoreArgs(args);
        if (messageSet is null || action is null || flagsRaw is null)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        if (unchangedSince is not null)
            session.CondstoreEnabled = true;

        var flagsList = flagsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!IsStoreAction(action))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid STORE action").ConfigureAwait(false);
            return;
        }
        if (!TryValidateFlagList(flagsList, out var flagFailure))
        {
            await writer.WriteLineAsync($"{tag} BAD {flagFailure}").ConfigureAwait(false);
            return;
        }

        ImapMessageSelection selection;
        if (string.Equals(messageSet, "$", StringComparison.Ordinal))
        {
            selection = new ImapMessageSelection(null, session.SavedSearchUids.ToList());
        }
        else if (ImapUidSetParser.TryParse(messageSet, out var ranges))
        {
            selection = new ImapMessageSelection(
                ranges.Select(range => new ImapMessageRange(range.Start, range.End)).ToList(),
                null);
        }
        else
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set").ConfigureAwait(false);
            return;
        }

        var mode = action[0] switch
        {
            '+' => ImapFlagMutationMode.Add,
            '-' => ImapFlagMutationMode.Remove,
            _ => ImapFlagMutationMode.Replace,
        };
        var commandName = useUid ? "UID STORE" : "STORE";
        List<string> responseLines;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            var result = await application.StoreFlagsAsync(new ImapStoreRequest(
                session.UserId,
                session.SelectedFolderId!.Value,
                useUid,
                selection,
                unchangedSince,
                mode,
                flagsList), ct).ConfigureAwait(false);
            responseLines = BuildStoreResponseLines(
                tag, commandName, result, useUid, action.Contains(".SILENT", StringComparison.Ordinal),
                session.CondstoreEnabled);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogCommandUnavailable(logger, exception, commandName, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] {commandName} backend unavailable").ConfigureAwait(false);
            return;
        }

        // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
        foreach (var line in responseLines)
#pragma warning restore HLQ012
            await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static List<string> BuildStoreResponseLines(
        string tag,
        string commandName,
        ImapStoreResult result,
        bool useUid,
        bool isSilent,
        bool condstoreEnabled)
    {
        if (result.Modified is null || result.Updated is null
            || result.Modified.Any(identifier => identifier < 1)
            || result.Modified.Zip(result.Modified.Skip(1))
                .Any(pair => pair.Second <= pair.First)
            || result.Updated.Any(message => message is null
                || message.Sequence < 1 || message.Uid < 1 || message.ModSeq < 1
                || message.Keywords is null
                || message.Keywords.Any(keyword => !IsValidImapKeyword(keyword)))
            || result.Updated.Zip(result.Updated.Skip(1))
                .Any(pair => pair.Second.Sequence <= pair.First.Sequence
                    || pair.Second.Uid <= pair.First.Uid))
        {
            throw new InvalidOperationException("The IMAP STORE result is invalid.");
        }

        if (result.Disposition != ImapStoreDisposition.Stored)
        {
            if (result.Modified.Count > 0 || result.Updated.Count > 0)
                throw new InvalidOperationException("The IMAP STORE result is invalid.");
            return result.Disposition switch
            {
                ImapStoreDisposition.FolderNotFound => [$"{tag} NO Mailbox not found"],
                ImapStoreDisposition.KeywordLimitExceeded =>
                    [$"{tag} NO [LIMIT] Too many keywords"],
                _ => throw new InvalidOperationException("The IMAP STORE result is invalid."),
            };
        }

        var lines = new List<string>();
        if (!isSilent)
        {
            foreach (ref readonly var message in CollectionsMarshal.AsSpan(result.Updated))
            {
                var uid = useUid ? $"UID {message.Uid} " : string.Empty;
                var modSeq = condstoreEnabled ? $" MODSEQ ({message.ModSeq})" : string.Empty;
                lines.Add($"* {message.Sequence} FETCH ({uid}FLAGS ({BuildFlagsList(message)}){modSeq})");
            }
        }
        var modified = result.Modified.Count > 0
            ? $"[MODIFIED {string.Join(',', result.Modified)}] "
            : string.Empty;
        lines.Add($"{tag} OK {modified}{commandName} completed");
        return lines;
    }

    private async Task HandleSearchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleSearchWithLimitAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);
    }

    private async Task HandleSearchWithLimitAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        using var slot = _searchCommandLimiter.TryAcquire();
        if (slot is null)
        {
            var (returnOptions, _) = ParseEsearchReturn(args);
            if (returnOptions?.Any(option => option.Equals("SAVE", StringComparison.OrdinalIgnoreCase)) == true)
                session.SavedSearchUids = [];
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent SEARCH commands").ConfigureAwait(false);
            return;
        }

        await HandleSearchCoreAsync(writer, tag, args, session, useUid, ct).ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleSearchCoreAsync(
#pragma warning restore MA0051
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
            await writer.WriteLineAsync($"{tag} BAD Unsupported SEARCH return option").ConfigureAwait(false);
            return;
        }

        var saveResults = normalizedReturnOptions?.Contains("SAVE") == true;
        if (session.Utf8Enabled && StartsWithCharsetSearchKey(searchCriteria))
        {
            await writer.WriteLineAsync($"{tag} BAD SEARCH CHARSET is not permitted after UTF8=ACCEPT").ConfigureAwait(false);
            return;
        }

        ImapSearchResult searchResult;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            searchResult = await application.SearchMessagesAsync(new ImapSearchRequest(
                session.UserId,
                session.SelectedFolderId!.Value,
                searchCriteria,
                session.SavedSearchUids.ToList(),
                session.Utf8Enabled), ct).ConfigureAwait(false);
            if (searchResult is null
                || searchResult.Matches is null
                || searchResult.HighestModSequence is < 0
                || !searchResult.FolderFound
                    && (searchResult.FailureResponse is not null
                        || searchResult.Matches.Count > 0
                        || searchResult.HighestModSequence is not null)
                || searchResult.FailureResponse is { } failure
                    && (failure.ContainsAny(['\r', '\n', '\0'])
                        || !failure.StartsWith("BAD ", StringComparison.OrdinalIgnoreCase)
                            && !failure.StartsWith("NO ", StringComparison.OrdinalIgnoreCase)
                        || searchResult.Matches.Count > 0
                        || searchResult.HighestModSequence is not null)
                || searchResult.Matches.Any(match => match is null
                    || match.Uid < 1 || match.SequenceNumber < 1)
                || searchResult.Matches.Zip(searchResult.Matches.Skip(1))
                    .Any(pair => pair.Second.Uid <= pair.First.Uid
                        || pair.Second.SequenceNumber <= pair.First.SequenceNumber))
            {
                throw new InvalidOperationException("The IMAP SEARCH result is invalid.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (saveResults)
                session.SavedSearchUids = [];
            LogSearchUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] SEARCH backend unavailable").ConfigureAwait(false);
            return;
        }

        if (!searchResult.FolderFound)
        {
            if (saveResults)
                session.SavedSearchUids = [];
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }
        if (searchResult.FailureResponse is not null)
        {
            if (saveResults
                && searchResult.FailureResponse.StartsWith("NO", StringComparison.OrdinalIgnoreCase))
            {
                session.SavedSearchUids = [];
            }
            await writer.WriteLineAsync($"{tag} {searchResult.FailureResponse}").ConfigureAwait(false);
            return;
        }

        var numbers = searchResult.Matches
            .Select(candidate => useUid ? candidate.Uid : candidate.SequenceNumber)
            .ToList();

        if (saveResults)
        {
            session.SavedSearchUids = SelectSavedSearchUids(
                normalizedReturnOptions!,
                searchResult.Matches);
        }

        var responseOptions = normalizedReturnOptions?
            .Where(option => !string.Equals(option, "SAVE", StringComparison.Ordinal))
            .ToArray();
        if (responseOptions is { Length: > 0 })
        {
            var result = BuildEsearchResult(
                responseOptions,
                numbers,
                searchResult.HighestModSequence);
            var uidMarker = useUid ? " UID" : string.Empty;
            var resultSuffix = result.Length > 0 ? $" {result}" : string.Empty;
            await writer.WriteLineAsync($"* ESEARCH (TAG \"{tag}\"){uidMarker}{resultSuffix}").ConfigureAwait(false);
        }
        else if (returnOptions is null)
        {
            var result = string.Join(' ', numbers);
            var numberSuffix = result.Length == 0 ? string.Empty : $" {result}";
            var modSequenceSuffix = searchResult.HighestModSequence is { } highestModSequence
                ? $" (MODSEQ {highestModSequence})"
                : string.Empty;
            await writer.WriteLineAsync($"* SEARCH{numberSuffix}{modSequenceSuffix}").ConfigureAwait(false);
        }

        var commandName = useUid ? "UID SEARCH" : "SEARCH";
        await writer.WriteLineAsync($"{tag} OK {commandName} completed").ConfigureAwait(false);
    }

    private async Task HandleExpungeAsync(StreamWriter writer, string tag, ImapSession session, CancellationToken ct)
    {
        var result = await TryExpungeDeletedAsync(writer, tag, "EXPUNGE", session, ct).ConfigureAwait(false);
        if (result is null)
            return;
        if (!result.FolderFound)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }

        await WriteExpungeMessagesAsync(writer, session, result).ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK EXPUNGE completed").ConfigureAwait(false);
    }

    private async Task<ImapExpungeResult?> TryExpungeDeletedAsync(
        StreamWriter writer,
        string tag,
        string operation,
        ImapSession session,
        CancellationToken ct,
        ImapUidSelection? selection = null)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            var result = await application.ExpungeDeletedAsync(new ImapExpungeRequest(
                session.UserId, session.SelectedFolderId!.Value, selection), ct).ConfigureAwait(false);
            if (result.Messages is null
                || (!result.FolderFound && result.Messages.Count > 0)
                || result.Messages.Any(message => message is null
                    || message.SequenceNumber < 1 || message.Uid < 1)
                || result.Messages.Zip(result.Messages.Skip(1))
                    .Any(pair => pair.Second.Uid <= pair.First.Uid
                        || pair.Second.SequenceNumber < pair.First.SequenceNumber))
            {
                throw new InvalidOperationException("The IMAP expunge result is invalid.");
            }
            return result;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogOperationUnavailable(logger, exception, operation, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] {operation} backend unavailable").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task WriteExpungeMessagesAsync(
        StreamWriter writer, ImapSession session, ImapExpungeResult result)
    {
        if (session.QresyncEnabled)
        {
            if (result.Messages.Count > 0)
                await writer.WriteLineAsync($"* VANISHED {FormatUidRange(
                    result.Messages.Select(message => message.Uid).ToList())}").ConfigureAwait(false);
        }
        else
        {
            // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
            foreach (var message in result.Messages)
#pragma warning restore HLQ012
                await writer.WriteLineAsync($"* {message.SequenceNumber} EXPUNGE").ConfigureAwait(false);
        }
    }

    private async Task HandleCopyAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleCopyWithLimitAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);
    }

    private async Task HandleCopyWithLimitAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        using var slot = _messageWriteCommandLimiter.TryAcquire();
        if (slot is null)
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent message writes").ConfigureAwait(false);
            return;
        }

        await HandleCopyCoreAsync(writer, tag, args, session, useUid, ct).ConfigureAwait(false);
    }

    private async Task HandleCopyCoreAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        var messageSet = args[..spaceIdx];
        if (!TryParseMailboxName(args[(spaceIdx + 1)..].Trim(), session.Utf8Enabled, out var destMailbox))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid destination mailbox name").ConfigureAwait(false);
            return;
        }

        ImapMessageSelection selection;
        if (string.Equals(messageSet, "$", StringComparison.Ordinal))
        {
            selection = new ImapMessageSelection(null, session.SavedSearchUids.ToList());
        }
        else if (ImapUidSetParser.TryParse(messageSet, out var ranges))
        {
            selection = new ImapMessageSelection(
                ranges.Select(range => new ImapMessageRange(range.Start, range.End)).ToList(),
                null);
        }
        else
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set").ConfigureAwait(false);
            return;
        }

        var commandName = useUid ? "UID COPY" : "COPY";
        List<string> responseLines;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            var result = await application.CopyMessagesAsync(new ImapCopyRequest(
                session.UserId, session.SelectedFolderId!.Value,
                destMailbox, useUid, selection), ct).ConfigureAwait(false);
            responseLines = BuildCopyResponseLines(tag, commandName, result);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogCommandUnavailable(logger, exception, commandName, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] {commandName} backend unavailable").ConfigureAwait(false);
            return;
        }

        // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
        foreach (var line in responseLines)
#pragma warning restore HLQ012
            await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static List<string> BuildCopyResponseLines(
        string tag, string commandName, ImapCopyResult result)
    {
        if (result.SourceUids is null || result.DestinationUids is null
            || result.SourceUids.Count != result.DestinationUids.Count
            || result.SourceUids.Any(uid => uid < 1)
            || result.DestinationUids.Any(uid => uid < 1)
            || result.SourceUids.Zip(result.SourceUids.Skip(1))
                .Any(pair => pair.Second <= pair.First)
            || result.DestinationUids.Zip(result.DestinationUids.Skip(1))
                .Any(pair => pair.Second <= pair.First))
        {
            throw new InvalidOperationException("The IMAP COPY result is invalid.");
        }

        if (result.Disposition != ImapCopyDisposition.Copied)
        {
            if (result.SourceUids.Count > 0 || result.DestinationUidValidity != 0)
                throw new InvalidOperationException("The IMAP COPY result is invalid.");
            return result.Disposition switch
            {
                ImapCopyDisposition.SourceNotFound => [$"{tag} NO Selected mailbox not found"],
                ImapCopyDisposition.DestinationNotFound =>
                    [$"{tag} NO [TRYCREATE] Destination mailbox not found"],
                ImapCopyDisposition.InvalidSourceSize =>
                    [$"{tag} NO [SERVERBUG] COPY source size is invalid"],
                ImapCopyDisposition.OverQuota =>
                    [$"{tag} NO [OVERQUOTA] COPY exceeds the mailbox quota"],
                _ => throw new InvalidOperationException("The IMAP COPY result is invalid."),
            };
        }

        if (result.DestinationUidValidity < 1)
            throw new InvalidOperationException("The IMAP COPY result is invalid.");
        return result.SourceUids.Count == 0
            ? [$"{tag} OK {commandName} completed"]
            : [$"{tag} OK [COPYUID {result.DestinationUidValidity} "
                + $"{FormatUidSet(result.SourceUids)} {FormatUidSet(result.DestinationUids)}] "
                + $"{commandName} completed"];
    }

    private async Task HandleUidAsync(
        StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        var subCommand = args[..spaceIdx].ToUpperInvariant();
        var subArgs = args[(spaceIdx + 1)..];

        switch (subCommand)
        {
            case "FETCH":
                await HandleUidFetchAsync(writer, tag, subArgs, session, ct).ConfigureAwait(false);
                break;
            case "SEARCH":
                await HandleUidSearchAsync(writer, tag, subArgs, session, ct).ConfigureAwait(false);
                break;
            case "STORE":
                await HandleUidStoreAsync(writer, tag, subArgs, session, ct).ConfigureAwait(false);
                break;
            case "COPY":
                await HandleUidCopyAsync(writer, tag, subArgs, session, ct).ConfigureAwait(false);
                break;
            case "MOVE":
                if (session.SelectedReadOnly)
                {
                    await writer.WriteLineAsync($"{tag} NO Mailbox is read-only").ConfigureAwait(false);
                    break;
                }
                await HandleUidMoveAsync(writer, tag, subArgs, session, ct).ConfigureAwait(false);
                break;
            case "SORT":
                await HandleSortAsync(writer, tag, subArgs, session, useUid: true, ct).ConfigureAwait(false);
                break;
            case "THREAD":
                await HandleThreadAsync(writer, tag, subArgs, session, useUid: true, ct).ConfigureAwait(false);
                break;
            case "EXPUNGE":
                if (session.SelectedReadOnly)
                {
                    await writer.WriteLineAsync($"{tag} NO Mailbox is read-only").ConfigureAwait(false);
                    break;
                }
                await HandleUidExpungeAsync(writer, tag, subArgs, session, ct).ConfigureAwait(false);
                break;
            default:
                await writer.WriteLineAsync($"{tag} BAD Unknown UID command").ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleUidFetchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleFetchWithLimitAsync(writer, tag, args, session, useUid: true, ct).ConfigureAwait(false);
    }

    private async Task HandleUidSearchAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        await HandleSearchWithLimitAsync(writer, tag, args, session, useUid: true, ct).ConfigureAwait(false);
    }

    private async Task HandleUidStoreAsync(StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
        => await HandleStoreCoreAsync(writer, tag, args, session, useUid: true, ct).ConfigureAwait(false);

    private async Task HandleUidCopyAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleCopyWithLimitAsync(writer, tag, args, session, useUid: true, ct).ConfigureAwait(false);
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
        await writer.WriteLineAsync($"* ENABLED {enabledStr}").ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK ENABLE completed").ConfigureAwait(false);
    }

    private async Task HandleSubscribeAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool subscribe, CancellationToken ct)
    {
        if (!TryParseMailboxName(args.Trim(), session.Utf8Enabled, out var mailboxName))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }

        ImapMailboxSubscriptionResult result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            result = await application.SetMailboxSubscriptionAsync(
                new ImapMailboxSubscriptionRequest(session.UserId, mailboxName, subscribe), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogSubscriptionUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Mailbox subscription unavailable").ConfigureAwait(false);
            return;
        }

        if (!result.Found)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }

        var cmd = subscribe ? "SUBSCRIBE" : "UNSUBSCRIBE";
        await writer.WriteLineAsync($"{tag} OK {cmd} completed").ConfigureAwait(false);
    }

    // ?? Helpers ??

    private async Task<List<MailboxFolderInfo>> GetUserFoldersAsync(
        Guid userId, CancellationToken ct, bool subscribedOnly = false)
    {
        using var scope = scopeFactory.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
        var result = await application.ListMailboxesAsync(
            new ImapMailboxListRequest(userId, subscribedOnly), ct).ConfigureAwait(false);
        return result.Mailboxes
            .Select(folder => new MailboxFolderInfo(
                folder.InboxName,
                folder.Domain,
                folder.FolderName,
                folder.IsPrimary,
                folder.IsSubscribed))
            .ToList();
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

    private static List<MailboxListEntry> BuildMailboxListEntries(
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
            for (var index = fullName.IndexOf('/', StringComparison.Ordinal); index >= 0; index = fullName.IndexOf('/', index + 1))
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

    private static string FormatUidSet(List<int> uids) =>
        uids.Count > 0 ? string.Join(',', uids) : "0";

    private static string FormatEmailObjectId(FetchPresentationMessage email) =>
        FormatEmailObjectId(email.Id, email.EmailObjectId);

    private static string FormatEmailObjectId(Guid id, string? emailObjectId) =>
        FormatObjectId('M', emailObjectId, id);

    private static string? FormatThreadObjectId(FetchPresentationMessage email) =>
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

    private static X509Certificate2 LoadCertificate(ImapListenerConfig config)
    {
        if (config.TlsCertificateKeyPath is not null)
            return X509Certificate2.CreateFromPemFile(config.TlsCertificatePath!, config.TlsCertificateKeyPath);
        return X509CertificateLoader.LoadPkcs12FromFile(config.TlsCertificatePath!, password: null);
    }

    private async Task<SslStream?> TryUpgradeToTlsAsync(
        Stream transport,
        ImapListenerConfig config,
        string remoteLabel,
        CancellationToken cancellationToken)
    {
        using var certificate = LoadCertificate(config);
        // Success transfers ownership to the caller; every failure disposes candidate.
#pragma warning disable CA2000
        var candidate = new SslStream(transport, leaveInnerStreamOpen: false);
#pragma warning restore CA2000
        try
        {
            if (await TryAuthenticateAsServerAsync(
                    candidate, certificate, remoteLabel, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
            await candidate.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static CompressedDuplexStream CreateCompressedStream(Stream transport)
    {
        // These two streams transfer to CompressedDuplexStream or are disposed on failure.
#pragma warning disable CA2000
        var deflate = new DeflateStream(transport, CompressionMode.Compress, leaveOpen: true);
#pragma warning restore CA2000
        try
        {
#pragma warning disable CA2000
            var inflate = new DeflateStream(transport, CompressionMode.Decompress, leaveOpen: true);
#pragma warning restore CA2000
            try
            {
                return new CompressedDuplexStream(transport, inflate, deflate);
            }
            catch
            {
                inflate.Dispose();
                throw;
            }
        }
        catch
        {
            deflate.Dispose();
            throw;
        }
    }

    private async Task<bool> TryAuthenticateAsServerAsync(
        SslStream stream,
        X509Certificate2 certificate,
        string remoteLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    // The public TLS minimum intentionally excludes TLS 1.0/1.1.
#pragma warning disable CA5398
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or AuthenticationException or SocketException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var exceptionType = exception.GetType().Name;
                LogTlsPeerEnded(logger, remoteLabel, exceptionType);
            }
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

        foreach (ref readonly var item in CollectionsMarshal.AsSpan(TokenizeFetchDataItems(fetchItems)))
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
            .Replace("BODY.PEEK[", "BODY[", StringComparison.Ordinal);
        return normalized.Contains("BODY[", StringComparison.Ordinal)
            || normalized.Contains("BODYSTRUCTURE", StringComparison.Ordinal)
            || BodyStandaloneRegex().IsMatch(normalized)
            || IsFetchMacro(normalized, "RFC822")
            || normalized.Contains("RFC822.HEADER", StringComparison.Ordinal)
            || normalized.Contains("RFC822.TEXT", StringComparison.Ordinal)
            || binaryRequests.Count > 0;
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static List<string> TokenizeFetchDataItems(string fetchItems)
#pragma warning restore MA0051
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
        foreach (ref readonly var item in CollectionsMarshal.AsSpan(TokenizeFetchDataItems(fetchItems)))
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

    private static bool IsFetchMacro(string items, string macro) => string.Equals(items, macro, StringComparison.Ordinal) || items.StartsWith(macro + " ", StringComparison.Ordinal) || items.EndsWith(" " + macro, StringComparison.Ordinal) || items.Contains(" " + macro + " ", StringComparison.Ordinal);

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
        if (string.Equals(fullPattern, "*", StringComparison.Ordinal))
            return true;
        if (string.Equals(fullPattern, "%", StringComparison.Ordinal))
            return !name.Contains('/', StringComparison.Ordinal);

        var regexPattern = "^" + Regex.Escape(fullPattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("%", "[^/]*", StringComparison.Ordinal) + "$";

        try
        {
            return Regex.IsMatch(name, regexPattern,
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static string BuildFetchResponse(
#pragma warning restore MA0051
        int seqNum,
        FetchPresentationMessage email,
        string fetchItems,
        bool useUid,
        ImapMimeMessage? mimeMessage,
        IReadOnlyList<BinaryFetchRequest> binaryRequests,
        Dictionary<string, ImapBinarySection> binarySections)
    {
        var items = fetchItems.ToUpperInvariant();
        var normalizedItems = items.Replace("BODY.PEEK[", "BODY[", StringComparison.Ordinal);
        var requestedDataItems = TokenizeFetchDataItems(fetchItems);
        var parts = new List<string>();

        var numericSectionMatch = NumericBodySectionRegex().Match(items);
        var partialMatch = BodyPartialFetchRegex().Match(items);
        int? partialOffset = null;
        int? partialCount = null;
        if (partialMatch.Success)
        {
            partialOffset = int.Parse(partialMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            partialCount = int.Parse(partialMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var isMacroAll = IsFetchMacro(normalizedItems, "ALL");
        var isMacroFast = IsFetchMacro(normalizedItems, "FAST");
        var isMacroFull = IsFetchMacro(normalizedItems, "FULL");

        if (normalizedItems.Contains("FLAGS", StringComparison.Ordinal) || isMacroAll || isMacroFast || isMacroFull)
            parts.Add($"FLAGS ({BuildFlagsList(email)})");

        if (normalizedItems.Contains("INTERNALDATE", StringComparison.Ordinal) || isMacroAll || isMacroFast || isMacroFull)
            parts.Add($"INTERNALDATE \"{email.ReceivedAt:dd-MMM-yyyy HH:mm:ss} +0000\"");

        if (normalizedItems.Contains("RFC822.SIZE", StringComparison.Ordinal) || isMacroAll || isMacroFast || isMacroFull)
        {
            var size = email.SizeBytes > 0 ? email.SizeBytes : MailWireEncoding.Instance.GetByteCount(BuildRfc822(email));
            parts.Add($"RFC822.SIZE {size}");
        }

        if (normalizedItems.Contains("ENVELOPE", StringComparison.Ordinal) || isMacroAll || isMacroFull)
        {
            parts.Add($"ENVELOPE {BuildEnvelope(email)}");
        }

        if (normalizedItems.Contains("BODY[]", StringComparison.Ordinal) || IsFetchMacro(normalizedItems, "RFC822"))
        {
            var rfc822 = BuildRfc822(email);
            var (data, origin) = ApplyPartial(rfc822, partialOffset, partialCount);
            var suffix = origin is not null ? $"<{origin}>" : "";
            parts.Add($"BODY[]{suffix} {{{MailWireEncoding.Instance.GetByteCount(data)}}}\r\n{data}");
        }

        if (normalizedItems.Contains("BODY[HEADER]", StringComparison.Ordinal) || normalizedItems.Contains("RFC822.HEADER", StringComparison.Ordinal))
        {
            var header = BuildRfc822Header(email);
            parts.Add($"BODY[HEADER] {{{MailWireEncoding.Instance.GetByteCount(header)}}}\r\n{header}");
        }

        if (normalizedItems.Contains("BODY[TEXT]", StringComparison.Ordinal) || normalizedItems.Contains("RFC822.TEXT", StringComparison.Ordinal))
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

        if (normalizedItems.Contains("BODYSTRUCTURE", StringComparison.Ordinal))
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
            else if (string.Equals(section, "1", StringComparison.Ordinal))
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

    // These indexed captures feed the existing FETCH section parser.
#pragma warning disable MA0023
    [GeneratedRegex(@"BODY(?:\.PEEK)?\[HEADER\.FIELDS\s*\(([^)]+)\)\]", RegexOptions.None, 100)]
    private static partial Regex HeaderFieldsRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[HEADER\.FIELDS\.NOT\s*\(([^)]+)\)\]", RegexOptions.None, 100)]
    private static partial Regex HeaderFieldsNotRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[((?:\d+\.)*\d+)(\.MIME)?\]", RegexOptions.None, 100)]
    private static partial Regex NumericBodySectionRegex();

    [GeneratedRegex(@"BODY(?:\.PEEK)?\[[^\]]*\]<(\d+)\.(\d+)>", RegexOptions.None, 100)]
    private static partial Regex BodyPartialFetchRegex();
#pragma warning restore MA0023

    [GeneratedRegex(@"(?<![.\[A-Z])BODY(?![.\[A-Z])", RegexOptions.None, 100)]
    private static partial Regex BodyStandaloneRegex();

    // Numeric capture indexes are part of the BINARY parser's existing wire mapping.
#pragma warning disable MA0023
    [GeneratedRegex(
        @"^BINARY(?:\.(PEEK))?\[((?:[1-9][0-9]*)(?:\.[1-9][0-9]*)*|)\](?:<([0-9]+)\.([1-9][0-9]*)>)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex BinaryContentDataItemRegex();
#pragma warning restore MA0023

    // Numeric capture indexes are likewise consumed by the BINARY.SIZE parser.
#pragma warning disable MA0023
    [GeneratedRegex(
        @"^BINARY\.SIZE\[((?:[1-9][0-9]*)(?:\.[1-9][0-9]*)*|)\]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex BinarySizeDataItemRegex();
#pragma warning restore MA0023

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

    private static string BuildFallbackBodyStructure(FetchPresentationMessage email, bool extended)
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

    private static string BuildFlagsList(FetchPresentationMessage email) => BuildFlagsList(
        email.IsRead,
        email.IsDeleted,
        email.IsFlagged,
        email.IsDraft,
        email.IsAnswered,
        email.Keywords);

    private static string BuildFlagsList(ImapChangedMessage message) => BuildFlagsList(
        message.IsRead,
        message.IsDeleted,
        message.IsFlagged,
        message.IsDraft,
        message.IsAnswered,
        message.Keywords);

    private static string BuildFlagsList(ImapIdleMessage message) => BuildFlagsList(
        message.IsRead,
        message.IsDeleted,
        message.IsFlagged,
        message.IsDraft,
        message.IsAnswered,
        message.Keywords);

    private static string BuildFlagsList(
        bool isRead,
        bool isDeleted,
        bool isFlagged,
        bool isDraft,
        bool isAnswered,
        string[]? keywords)
    {
        var flags = new List<string>();
        if (isRead) flags.Add("\\Seen");
        if (isDeleted) flags.Add("\\Deleted");
        if (isFlagged) flags.Add("\\Flagged");
        if (isDraft) flags.Add("\\Draft");
        if (isAnswered) flags.Add("\\Answered");
        flags.AddRange((keywords ?? [])
            .Where(IsValidImapKeyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
        return string.Join(' ', flags);
    }

    private static string BuildRfc822(FetchPresentationMessage email)
    {
        if (email.RawMessage is not null)
            return MailWireEncoding.Instance.GetString(email.RawMessage);
        if (email.RawHeaders is not null)
            return email.RawHeaders + "\r\n\r\n" + email.Body;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"From: {email.Sender}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"To: {email.Recipient}\r\n");
        if (!string.IsNullOrEmpty(email.Cc))
            sb.Append(CultureInfo.InvariantCulture, $"Cc: {email.Cc}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Subject: {email.Subject}\r\n");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Date: {email.ReceivedAt:ddd, dd MMM yyyy HH:mm:ss +0000}\r\n");
        if (!string.IsNullOrEmpty(email.MessageId))
            sb.Append(CultureInfo.InvariantCulture, $"Message-ID: {email.MessageId}\r\n");
        if (!string.IsNullOrEmpty(email.InReplyTo))
            sb.Append(CultureInfo.InvariantCulture, $"In-Reply-To: {email.InReplyTo}\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        sb.Append("\r\n");
        sb.Append(email.Body);
        return sb.ToString();
    }

    private static void ApplyTransientRawMessage(
        FetchPresentationMessage email, byte[] rawMessage)
    {
        email.RawMessage = rawMessage;
        var raw = MailWireEncoding.Instance.GetString(rawMessage);
        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator >= 0)
        {
            email.RawHeaders = raw[..separator];
            email.Body = raw[(separator + 4)..];
            return;
        }

        separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator >= 0)
        {
            email.RawHeaders = raw[..separator];
            email.Body = raw[(separator + 2)..];
            return;
        }

        email.RawHeaders = raw;
        email.Body = string.Empty;
    }

    private static string BuildRfc822Header(FetchPresentationMessage email)
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
        sb.Append(CultureInfo.InvariantCulture, $"From: {email.Sender}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"To: {email.Recipient}\r\n");
        if (!string.IsNullOrEmpty(email.Cc))
            sb.Append(CultureInfo.InvariantCulture, $"Cc: {email.Cc}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Subject: {email.Subject}\r\n");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Date: {email.ReceivedAt:ddd, dd MMM yyyy HH:mm:ss +0000}\r\n");
        if (!string.IsNullOrEmpty(email.MessageId))
            sb.Append(CultureInfo.InvariantCulture, $"Message-ID: {email.MessageId}\r\n");
        if (!string.IsNullOrEmpty(email.InReplyTo))
            sb.Append(CultureInfo.InvariantCulture, $"In-Reply-To: {email.InReplyTo}\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static bool IsStoreAction(string action) =>
        action is "FLAGS" or "FLAGS.SILENT" or "+FLAGS" or "+FLAGS.SILENT" or "-FLAGS" or "-FLAGS.SILENT";

    private static bool TryValidateFlagList(IEnumerable<string> flags, out string failure)
        => ImapFlagSyntax.TryValidate(flags as IReadOnlyList<string> ?? flags.ToArray(), out failure);

    private static bool IsValidImapKeyword(string keyword)
        => ImapFlagSyntax.IsValidKeyword(keyword);

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static bool TryParseMessageSet(
#pragma warning restore MA0051
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

            var separator = part.IndexOf(':', StringComparison.Ordinal);
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

        foreach (ref readonly var range in CollectionsMarshal.AsSpan(parsedRanges))
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
        if (string.Equals(value, "*", StringComparison.Ordinal))
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

        return int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out identifier) && identifier > 0;
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

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static bool TryTokenizeListArguments(
#pragma warning restore MA0051
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

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static bool TryParseListCommand(
#pragma warning restore MA0051
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

        if (!TryTokenizeListArguments(args, out var tokens) || tokens.Count == 0)
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
        foreach (ref readonly var wirePattern in CollectionsMarshal.AsSpan(wirePatterns))
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
        foreach (ref readonly var selectionOption in CollectionsMarshal.AsSpan(selectionOptions))
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
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static (string? sequenceSet, long? unchangedSince, string? action, string? flagsRaw) ParseStoreArgs(string args)
    {
        var parts = args.Split(' ', 3);
        if (parts.Length < 3)
            return (null, null, null, null);

        var sequenceSet = parts[0];

        if (parts[1].StartsWith('('))
        {
            var rest = parts[1] + " " + parts[2];
            var closeParenIdx = rest.IndexOf(')', StringComparison.Ordinal);
            if (closeParenIdx < 0)
                return (null, null, null, null);

            var modifier = rest[1..closeParenIdx];
            var modParts = modifier.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            long? unchangedSince = null;
            if (modParts.Length == 2 &&
                modParts[0].Equals("UNCHANGEDSINCE", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(modParts[1], System.Globalization.CultureInfo.InvariantCulture, out var modSeq))
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
            await writer.WriteLineAsync($"{tag} BAD Invalid mailbox name").ConfigureAwait(false);
            return;
        }

        ImapQuotaResult quota;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            quota = await application.GetQuotaAsync(
                new ImapQuotaRequest(session.UserId, mailboxName), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogQuotaUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Quota unavailable").ConfigureAwait(false);
            return;
        }

        if (!quota.MailboxFound)
        {
            await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Mailbox not found").ConfigureAwait(false);
            return;
        }

        await writer.WriteLineAsync(
            $"* QUOTAROOT \"{EscapeImapString(FormatWireMailboxName(mailboxName, session.Utf8Enabled))}\" \"\"").ConfigureAwait(false);
        if (quota.LimitBytes > 0)
            await writer.WriteLineAsync(
                $"* QUOTA \"\" (STORAGE {ToQuotaStorageUnits(quota.UsedBytes)} {ToQuotaStorageUnits(quota.LimitBytes)})").ConfigureAwait(false);
        else
            await writer.WriteLineAsync("* QUOTA \"\" ()").ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK GETQUOTAROOT completed").ConfigureAwait(false);
    }

    private async Task HandleGetQuotaAsync(
        StreamWriter writer, string tag, string args, ImapSession session, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(UnquoteArg(args.Trim())))
        {
            await writer.WriteLineAsync($"{tag} NO [NONEXISTENT] Quota root not found").ConfigureAwait(false);
            return;
        }

        ImapQuotaResult quota;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            quota = await application.GetQuotaAsync(new ImapQuotaRequest(session.UserId, null), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogQuotaUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Quota unavailable").ConfigureAwait(false);
            return;
        }

        if (quota.LimitBytes > 0)
            await writer.WriteLineAsync(
                $"* QUOTA \"\" (STORAGE {ToQuotaStorageUnits(quota.UsedBytes)} {ToQuotaStorageUnits(quota.LimitBytes)})").ConfigureAwait(false);
        else
            await writer.WriteLineAsync("* QUOTA \"\" ()").ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK GETQUOTA completed").ConfigureAwait(false);
    }

    private static long ToQuotaStorageUnits(long bytes) =>
        bytes <= 0 ? 0 : 1 + (bytes - 1) / 1024;

    private static string BuildEnvelope(FetchPresentationMessage email)
    {
        var (senderLocal, senderDomain) = SplitAddress(email.Sender);
        var (rcptLocal, rcptDomain) = SplitAddress(email.Recipient);

        var date = email.ReceivedAt.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);

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
        var atIdx = address.IndexOf('@', StringComparison.Ordinal);
        return atIdx >= 0
            ? (address[..atIdx], address[(atIdx + 1)..])
            : (address, string.Empty);
    }

    private static string FilterHeaders(
        FetchPresentationMessage email, string[] requestedFields)
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
            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
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

    private static string FilterHeadersNot(
        FetchPresentationMessage email, string[] excludedFields)
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
            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
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

    private enum SearchTokenKind
    {
        Atom,
        OpenParenthesis,
        CloseParenthesis,
    }

    private readonly record struct SearchToken(SearchTokenKind Kind, string Value);

    // — ESEARCH helpers (RFC 4731) —

    private static bool StartsWithCharsetSearchKey(string criteria)
    {
        var trimmed = criteria.TrimStart();
        return trimmed.Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("CHARSET ", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<int> SelectSavedSearchUids(
        IReadOnlyCollection<string> returnOptions,
        List<ImapSearchMatch> matches)
    {
        var saveAll = returnOptions.Contains("ALL", StringComparer.Ordinal)
            || returnOptions.Contains("COUNT", StringComparer.Ordinal)
            || !returnOptions.Contains("MIN", StringComparer.Ordinal) && !returnOptions.Contains("MAX", StringComparer.Ordinal);
        if (saveAll)
            return matches.Select(match => match.Uid).ToHashSet();

        var saved = new HashSet<int>();
        if (matches.Count == 0)
            return saved;

        if (returnOptions.Contains("MIN", StringComparer.Ordinal))
            saved.Add(matches[0].Uid);
        if (returnOptions.Contains("MAX", StringComparer.Ordinal))
            saved.Add(matches[^1].Uid);
        return saved;
    }

    private static (string[]? returnOpts, string searchCriteria) ParseEsearchReturn(string args)
    {
        var trimmed = args.TrimStart();
        if (trimmed.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase))
        {
            var openParen = trimmed.IndexOf('(', StringComparison.Ordinal);
            var closeParen = trimmed.IndexOf(')', StringComparison.Ordinal);
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
        var opts = new HashSet<string>(returnOpts.Select(o => o.ToUpperInvariant()), StringComparer.Ordinal);

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
        using var slot = _searchCommandLimiter.TryAcquire();
        if (slot is null)
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent search commands").ConfigureAwait(false);
            return;
        }

        await HandleSortCoreAsync(writer, tag, args, session, useUid, ct).ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleSortCoreAsync(
#pragma warning restore MA0051
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        if (!TryParseSortArguments(
                args,
                out var sortCriteria,
                out var charset,
                out var searchCriteria))
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        if (!charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync(
                $"{tag} NO [BADCHARSET (US-ASCII UTF-8)] Unsupported sort charset").ConfigureAwait(false);
            return;
        }

        ImapSortResult sortResult;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            sortResult = await application.SortMessagesAsync(new ImapSortRequest(
                session.UserId,
                session.SelectedFolderId!.Value,
                searchCriteria,
                session.SavedSearchUids.ToList(),
                session.Utf8Enabled,
                charset,
                sortCriteria.ToList()), ct).ConfigureAwait(false);
            if (sortResult is null
                || sortResult.SortedMatches is null
                || sortResult.HighestModSequence is < 0
                || !sortResult.FolderFound
                    && (sortResult.FailureResponse is not null
                        || sortResult.SortedMatches.Count > 0
                        || sortResult.HighestModSequence is not null)
                || sortResult.FailureResponse is { } failure
                    && (failure.ContainsAny(['\r', '\n', '\0'])
                        || !failure.StartsWith("BAD ", StringComparison.OrdinalIgnoreCase)
                            && !failure.StartsWith("NO ", StringComparison.OrdinalIgnoreCase)
                        || sortResult.SortedMatches.Count > 0
                        || sortResult.HighestModSequence is not null)
                || sortResult.SortedMatches.Any(match => match is null
                    || match.Uid < 1 || match.SequenceNumber < 1)
                || sortResult.SortedMatches.Select(match => match.Uid).Distinct().Count()
                    != sortResult.SortedMatches.Count
                || sortResult.SortedMatches.Select(match => match.SequenceNumber).Distinct().Count()
                    != sortResult.SortedMatches.Count)
            {
                throw new InvalidOperationException("The IMAP SORT result is invalid.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogSortUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] SORT backend unavailable").ConfigureAwait(false);
            return;
        }

        if (!sortResult.FolderFound)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }
        if (sortResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {sortResult.FailureResponse}").ConfigureAwait(false);
            return;
        }

        var result = string.Join(' ', sortResult.SortedMatches
            .Select(message => useUid ? message.Uid : message.SequenceNumber));
        var resultSuffix = result.Length == 0 ? string.Empty : $" {result}";
        var modSequenceSuffix = sortResult.HighestModSequence is { } highestModSequence
            ? $" (MODSEQ {highestModSequence})"
            : string.Empty;
        await writer.WriteLineAsync($"* SORT{resultSuffix}{modSequenceSuffix}").ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK {(useUid ? "UID SORT" : "SORT")} completed").ConfigureAwait(false);
    }

    private async Task HandleThreadAsync(
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        using var slot = _searchCommandLimiter.TryAcquire();
        if (slot is null)
        {
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent search commands").ConfigureAwait(false);
            return;
        }

        await HandleThreadCoreAsync(writer, tag, args, session, useUid, ct).ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleThreadCoreAsync(
#pragma warning restore MA0051
        StreamWriter writer, string tag, string args, ImapSession session, bool useUid, CancellationToken ct)
    {
        if (!TryParseThreadArguments(
                args,
                out var algorithm,
                out var charset,
                out var searchCriteria))
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        if (algorithm is not "REFERENCES" and not "ORDEREDSUBJECT")
        {
            await writer.WriteLineAsync($"{tag} BAD Unknown threading algorithm").ConfigureAwait(false);
            return;
        }
        if (!charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync(
                $"{tag} NO [BADCHARSET (US-ASCII UTF-8)] Unsupported thread charset").ConfigureAwait(false);
            return;
        }

        ImapThreadResult threadResult;
        string threads;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            threadResult = await application.ThreadMessagesAsync(new ImapThreadRequest(
                session.UserId,
                session.SelectedFolderId!.Value,
                searchCriteria,
                session.SavedSearchUids.ToList(),
                session.Utf8Enabled,
                charset,
                string.Equals(algorithm, "REFERENCES", StringComparison.Ordinal)
                    ? ImapThreadAlgorithm.References
                    : ImapThreadAlgorithm.OrderedSubject,
                useUid), ct).ConfigureAwait(false);
            if (threadResult is null
                || threadResult.Nodes is null
                || !threadResult.FolderFound
                    && (threadResult.FailureResponse is not null
                        || threadResult.Nodes.Count > 0)
                || threadResult.FailureResponse is { } failure
                    && (failure.ContainsAny(['\r', '\n', '\0'])
                        || !failure.StartsWith("BAD ", StringComparison.OrdinalIgnoreCase)
                            && !failure.StartsWith("NO ", StringComparison.OrdinalIgnoreCase)
                        || threadResult.Nodes.Count > 0))
            {
                throw new InvalidOperationException("The IMAP THREAD result is invalid.");
            }
            threads = RenderThreadNodes(threadResult.Nodes);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogThreadUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] THREAD backend unavailable").ConfigureAwait(false);
            return;
        }

        if (!threadResult.FolderFound)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }
        if (threadResult.FailureResponse is not null)
        {
            await writer.WriteLineAsync($"{tag} {threadResult.FailureResponse}").ConfigureAwait(false);
            return;
        }

        var responseSuffix = threads.Length == 0 ? string.Empty : $" {threads}";
        await writer.WriteLineAsync($"* THREAD{responseSuffix}").ConfigureAwait(false);
        await writer.WriteLineAsync(
            $"{tag} OK {(useUid ? "UID THREAD" : "THREAD")} completed").ConfigureAwait(false);
    }

    private static bool TryParseSortArguments(
        string args,
        out List<ImapSortCriterion> criteria,
        out string charset,
        out string searchCriteria)
    {
        criteria = [];
        charset = string.Empty;
        searchCriteria = string.Empty;

        var value = args.TrimStart(' ');
        if (value.Length < 3 || value[0] != '(')
            return false;
        var closeParen = value.IndexOf(')', StringComparison.Ordinal);
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
        var separator = value.IndexOf(' ', StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        algorithm = value[..separator].ToUpperInvariant();
        return TryReadCharsetAndSearchCriteria(
            value[(separator + 1)..].TrimStart(' '),
            out charset,
            out searchCriteria);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static bool TryReadCharsetAndSearchCriteria(
#pragma warning restore MA0051
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

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private static string RenderThreadNodes(List<ImapThreadNode> nodes)
#pragma warning restore MA0051
    {
        var children = new List<int>[nodes.Count];
        var roots = new List<int>();
        var identifiers = new HashSet<int>();
        // Indexes are graph identifiers; foreach cannot assign the parallel child array.
#pragma warning disable HLQ013
        for (var index = 0; index < nodes.Count; index++)
            children[index] = [];
#pragma warning restore HLQ013

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node is null
                || node.ParentIndex < -1
                || node.ParentIndex >= index
                || node.Identifier is { } identifier
                    && (identifier < 1 || !identifiers.Add(identifier)))
            {
                throw new InvalidOperationException("The IMAP THREAD graph is invalid.");
            }
            if (node.ParentIndex == -1)
                roots.Add(index);
            else
                children[node.ParentIndex].Add(index);
        }

        var result = new StringBuilder();
        foreach (ref readonly var root in CollectionsMarshal.AsSpan(roots))
        {
            var actions = new Stack<(int Index, bool Close)>();
            actions.Push((root, false));
            while (actions.Count > 0)
            {
                var action = actions.Pop();
                if (action.Close)
                {
                    result.Append(')');
                    continue;
                }

                var nodeIndex = action.Index;
                var node = nodes[nodeIndex];
                result.Append('(');
                actions.Push((-1, true));
                if (node.Identifier is null)
                {
                    if (children[nodeIndex].Count == 0)
                        throw new InvalidOperationException("The IMAP THREAD graph is invalid.");
                    for (var index = children[nodeIndex].Count - 1; index >= 0; index--)
                        actions.Push((children[nodeIndex][index], false));
                    continue;
                }

                result.Append(node.Identifier.Value);
                var tail = nodeIndex;
                while (children[tail].Count == 1
                       && nodes[children[tail][0]].Identifier is { } childIdentifier)
                {
                    tail = children[tail][0];
                    result.Append(' ').Append(childIdentifier);
                }
                if (children[tail].Count == 0)
                    continue;

                result.Append(' ');
                for (var index = children[tail].Count - 1; index >= 0; index--)
                    actions.Push((children[tail][index], false));
            }
        }
        return result.ToString();
    }

    private async Task HandleUidExpungeAsync(
        StreamWriter writer, string tag, string uidSetArg, ImapSession session, CancellationToken ct)
    {
        ImapUidSelection selection;
        if (string.Equals(uidSetArg, "$", StringComparison.Ordinal))
        {
            selection = new ImapUidSelection(null, session.SavedSearchUids.ToList());
        }
        else if (ImapUidSetParser.TryParse(uidSetArg, out var ranges))
        {
            selection = new ImapUidSelection(
                ranges.Select(range => new ImapUidRange(range.Start, range.End)).ToList(), null);
        }
        else
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set").ConfigureAwait(false);
            return;
        }

        var result = await TryExpungeDeletedAsync(
            writer, tag, "UID EXPUNGE", session, ct, selection).ConfigureAwait(false);
        if (result is null)
            return;
        if (!result.FolderFound)
        {
            await writer.WriteLineAsync($"{tag} NO Mailbox not found").ConfigureAwait(false);
            return;
        }

        await WriteExpungeMessagesAsync(writer, session, result).ConfigureAwait(false);
        await writer.WriteLineAsync($"{tag} OK UID EXPUNGE completed").ConfigureAwait(false);
    }

    private async Task HandleMoveAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleMoveCoreAsync(writer, tag, args, session, useUid: false, ct).ConfigureAwait(false);
    }

    private async Task HandleUidMoveAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        CancellationToken ct)
    {
        await HandleMoveCoreAsync(writer, tag, args, session, useUid: true, ct).ConfigureAwait(false);
    }

    private async Task HandleMoveCoreAsync(
        StreamWriter writer,
        string tag,
        string args,
        ImapSession session,
        bool useUid,
        CancellationToken ct)
    {
        var spaceIdx = args.IndexOf(' ', StringComparison.Ordinal);
        if (spaceIdx <= 0)
        {
            await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
            return;
        }

        var messageSet = args[..spaceIdx];
        if (!TryParseMailboxName(args[(spaceIdx + 1)..].Trim(), session.Utf8Enabled, out var destMailbox))
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid destination mailbox name").ConfigureAwait(false);
            return;
        }

        ImapMessageSelection selection;
        if (string.Equals(messageSet, "$", StringComparison.Ordinal))
        {
            selection = new ImapMessageSelection(null, session.SavedSearchUids.ToList());
        }
        else if (ImapUidSetParser.TryParse(messageSet, out var ranges))
        {
            selection = new ImapMessageSelection(
                ranges.Select(range => new ImapMessageRange(range.Start, range.End)).ToList(),
                null);
        }
        else
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid message set").ConfigureAwait(false);
            return;
        }

        var commandName = useUid ? "UID MOVE" : "MOVE";
        List<string> responseLines;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
            var result = await application.MoveMessagesAsync(new ImapMoveRequest(
                session.UserId, session.SelectedFolderId!.Value,
                destMailbox, useUid, selection), ct).ConfigureAwait(false);
            responseLines = BuildMoveResponseLines(tag, commandName, result, session.QresyncEnabled);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogCommandUnavailable(logger, exception, commandName, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] {commandName} backend unavailable").ConfigureAwait(false);
            return;
        }

        // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
        foreach (var line in responseLines)
#pragma warning restore HLQ012
            await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static List<string> BuildMoveResponseLines(
        string tag, string commandName, ImapMoveResult result, bool qresyncEnabled)
    {
        if (result.SourceUids is null || result.DestinationUids is null
            || result.ExpungeSequenceNumbers is null
            || result.SourceUids.Count != result.DestinationUids.Count
            || result.SourceUids.Count != result.ExpungeSequenceNumbers.Count
            || result.SourceUids.Any(uid => uid < 1)
            || result.DestinationUids.Any(uid => uid < 1)
            || result.ExpungeSequenceNumbers.Any(sequence => sequence < 1)
            || result.SourceUids.Zip(result.SourceUids.Skip(1))
                .Any(pair => pair.Second <= pair.First)
            || result.DestinationUids.Zip(result.DestinationUids.Skip(1))
                .Any(pair => pair.Second <= pair.First)
            || result.ExpungeSequenceNumbers.Zip(result.ExpungeSequenceNumbers.Skip(1))
                .Any(pair => pair.Second < pair.First))
        {
            throw new InvalidOperationException("The IMAP MOVE result is invalid.");
        }

        if (result.Disposition != ImapMoveDisposition.Moved)
        {
            if (result.SourceUids.Count > 0 || result.DestinationUidValidity != 0)
                throw new InvalidOperationException("The IMAP MOVE result is invalid.");
            return result.Disposition switch
            {
                ImapMoveDisposition.SourceNotFound => [$"{tag} NO Selected mailbox not found"],
                ImapMoveDisposition.DestinationNotFound =>
                    [$"{tag} NO [TRYCREATE] Destination mailbox not found"],
                _ => throw new InvalidOperationException("The IMAP MOVE result is invalid."),
            };
        }

        if (result.DestinationUidValidity < 1)
            throw new InvalidOperationException("The IMAP MOVE result is invalid.");
        if (result.SourceUids.Count == 0)
            return [$"{tag} OK {commandName} completed"];

        var lines = new List<string>();
        if (qresyncEnabled)
        {
            lines.Add($"* VANISHED {FormatUidRange(result.SourceUids)}");
        }
        else
        {
            lines.AddRange(result.ExpungeSequenceNumbers
                .Select(sequence => $"* {sequence} EXPUNGE"));
        }
        lines.Add($"{tag} OK [COPYUID {result.DestinationUidValidity} "
            + $"{FormatUidSet(result.SourceUids)} {FormatUidSet(result.DestinationUids)}] "
            + $"{commandName} completed");
        return lines;
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
        using var slot = _messageWriteCommandLimiter.TryAcquire();
        if (slot is null)
        {
            if (NonSynchronizingLiteralRegex().IsMatch(args))
            {
                await writer.WriteLineAsync("* BYE Too many concurrent APPEND commands").ConfigureAwait(false);
                session.State = ImapState.Logout;
            }
            else
            {
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] Too many concurrent APPEND commands").ConfigureAwait(false);
            }

            return;
        }

        await HandleAppendCoreAsync(
            reader,
            writer,
            tag,
            args,
            session,
            maximumMessageSize,
            timeout,
            connectionTimeoutSeconds).ConfigureAwait(false);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleAppendCoreAsync(
#pragma warning restore MA0051
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
        var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();

        var remaining = args;
        var pendingMessages = new List<ImapAppendMessage>();
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
                    await writer.WriteLineAsync($"{tag} BAD Syntax error").ConfigureAwait(false);
                    return;
                }
                break;
            }

            targetMailbox ??= mailboxName;

            if (!TryValidateFlagList(flags, out var flagFailure))
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    $"[CANNOT] {flagFailure}").ConfigureAwait(false);
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
                    "[LIMIT] APPEND contains too many keywords").ConfigureAwait(false);
                return;
            }

            if (pendingMessages.Count >= MaximumMultiAppendMessages)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "[LIMIT] APPEND contains too many messages").ConfigureAwait(false);
                return;
            }

            if (literalSize == 0)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "APPEND requires a non-empty message").ConfigureAwait(false);
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
                    "[TOOBIG] APPEND exceeds the command size limit").ConfigureAwait(false);
                return;
            }

            var nextPendingBytes = pendingBytes + literalSize.Value;
            ImapAppendPreflightResult preflight;
            try
            {
                preflight = await application.CheckAppendCapacityAsync(
                    new ImapAppendPreflightRequest(
                        session.UserId, targetMailbox, nextPendingBytes), ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                LogAppendPreflightUnavailable(logger, exception, session.UserId);
                await RejectAppendBeforeLiteralAsync(
                    writer, tag, session, isLiteralPlus,
                    "[UNAVAILABLE] APPEND backend unavailable").ConfigureAwait(false);
                return;
            }

            if (preflight?.Disposition == ImapAppendPreflightDisposition.MailboxNotFound)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer, tag, session, isLiteralPlus,
                    "[TRYCREATE] Mailbox not found").ConfigureAwait(false);
                return;
            }
            if (preflight?.Disposition == ImapAppendPreflightDisposition.OverQuota)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer,
                    tag,
                    session,
                    isLiteralPlus,
                    "[OVERQUOTA] APPEND exceeds the mailbox quota").ConfigureAwait(false);
                return;
            }
            if (preflight?.Disposition != ImapAppendPreflightDisposition.Ready)
            {
                await RejectAppendBeforeLiteralAsync(
                    writer, tag, session, isLiteralPlus,
                    "[UNAVAILABLE] APPEND backend unavailable").ConfigureAwait(false);
                return;
            }

            if (!isLiteralPlus)
                await writer.WriteLineAsync("+ Ready for literal data").ConfigureAwait(false);

            var buffer = new char[literalSize.Value];
            var totalRead = 0;
            while (totalRead < literalSize.Value)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                var read = await reader.ReadAsync(buffer.AsMemory(totalRead, literalSize.Value - totalRead), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The APPEND literal ended before its declared size.");
                totalRead += read;
            }

            var messageData = new string(buffer, 0, totalRead);
            if (messageData.Contains('\0', StringComparison.Ordinal))
            {
                if (!await ConsumeRejectedAppendRemainderAsync(
                        reader,
                        writer,
                        session,
                        timeout,
                        connectionTimeoutSeconds,
                        ct).ConfigureAwait(false))
                {
                    return;
                }
                var response = isLiteral8
                    ? "[UNKNOWN-CTE] Binary APPEND storage is not supported"
                    : "APPEND content contains a NUL byte";
                await writer.WriteLineAsync($"{tag} NO {response}").ConfigureAwait(false);
                return;
            }

            if (!TryPrepareAppendMessageForParsing(
                    messageData,
                    session.Utf8Enabled,
                    out _,
                    out var utf8Failure))
            {
                if (!await ConsumeRejectedAppendRemainderAsync(
                        reader,
                        writer,
                        session,
                        timeout,
                        connectionTimeoutSeconds,
                        ct).ConfigureAwait(false))
                {
                    return;
                }
                await writer.WriteLineAsync($"{tag} NO [CANNOT] {utf8Failure}").ConfigureAwait(false);
                return;
            }

            pendingMessages.Add(new ImapAppendMessage(
                Guid.CreateVersion7(),
                flags.ToArray(),
                internalDate,
                MailWireEncoding.Instance.GetBytes(messageData)));
            pendingBytes = nextPendingBytes;

            timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
            var nextLineResult = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
            if (nextLineResult.IsTooLong)
            {
                await writer.WriteLineAsync("* BYE APPEND continuation is too long").ConfigureAwait(false);
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
                await writer.WriteLineAsync($"{tag} BAD Invalid APPEND continuation").ConfigureAwait(false);
                return;
            }

            remaining =
                $"\"{EscapeImapString(FormatWireMailboxName(targetMailbox, session.Utf8Enabled))}\" {nextLine.Trim()}";
        }

        ImapAppendResult result;
        try
        {
            result = await application.AppendMessagesAsync(new ImapAppendRequest(
                session.UserId, targetMailbox!, session.Utf8Enabled, pendingMessages), ct).ConfigureAwait(false);
            if (!Enum.IsDefined(result.Disposition)
                || result.Uids is null
                || result.Disposition == ImapAppendDisposition.Appended
                    && (result.UidValidity < 1
                        || result.Uids.Count != pendingMessages.Count
                        || result.Uids.Any(uid => uid < 1)
                        || result.Uids.Zip(result.Uids.Skip(1))
                            .Any(pair => pair.Second <= pair.First))
                || result.Disposition != ImapAppendDisposition.Appended
                    && result.Uids.Count != 0)
            {
                throw new InvalidOperationException("The IMAP APPEND result is invalid.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogAppendUnavailable(logger, exception, session.UserId);
            await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] APPEND backend unavailable").ConfigureAwait(false);
            return;
        }

        switch (result.Disposition)
        {
            case ImapAppendDisposition.MailboxNotFound:
                await writer.WriteLineAsync($"{tag} NO [TRYCREATE] Mailbox not found").ConfigureAwait(false);
                break;
            case ImapAppendDisposition.OverQuota:
                await writer.WriteLineAsync($"{tag} NO [OVERQUOTA] APPEND exceeds the mailbox quota").ConfigureAwait(false);
                break;
            case ImapAppendDisposition.InvalidFlags:
                await writer.WriteLineAsync($"{tag} NO [LIMIT] APPEND flags are invalid").ConfigureAwait(false);
                break;
            case ImapAppendDisposition.InvalidContent:
                await writer.WriteLineAsync($"{tag} NO [CANNOT] APPEND content is invalid").ConfigureAwait(false);
                break;
            case ImapAppendDisposition.Appended:
                var uidSetStr = FormatUidRange(result.Uids);
                await writer.WriteLineAsync(
                    $"{tag} OK [APPENDUID {result.UidValidity} {uidSetStr}] APPEND completed").ConfigureAwait(false);
                break;
        }
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
        var remainder = await reader.ReadLineAsync(MaximumCommandLineCharacters, ct).ConfigureAwait(false);
        if (remainder.IsTooLong)
        {
            await writer.WriteLineAsync("* BYE APPEND continuation is too long").ConfigureAwait(false);
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
            await writer.WriteLineAsync("* BYE APPEND rejected with pending continuation data").ConfigureAwait(false);
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
            await writer.WriteLineAsync($"* BYE {response}").ConfigureAwait(false);
            session.State = ImapState.Logout;
            return;
        }

        await writer.WriteLineAsync($"{tag} NO {response}").ConfigureAwait(false);
    }

    [GeneratedRegex(@"\{\d+\+\}\s*$", RegexOptions.None, 100)]
    private static partial Regex NonSynchronizingLiteralRegex();

    // The literal parser consumes both numeric captures by index.
#pragma warning disable MA0023
    [GeneratedRegex(@"\{([0-9]+)(\+)?\}$", RegexOptions.None, 100)]
    private static partial Regex CommandLiteralRegex();
#pragma warning restore MA0023

    // Preserve the complete APPEND literal grammar and rejection order.
#pragma warning disable MA0051
    private static (
        string? mailboxName,
        List<string> flags,
        DateTime? internalDate,
        int? literalSize,
        bool isLiteral8,
        bool isLiteralPlus) ParseAppendArgs(
#pragma warning restore MA0051
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
                if (!token.Contains(')', StringComparison.Ordinal))
                {
                    while (i + 1 < tokens.Count)
                    {
                        i++;
                        flagStr += " " + tokens[i].TrimEnd(')');
                        if (tokens[i].Contains(')', StringComparison.Ordinal)) break;
                    }
                }
                flags.AddRange(flagStr.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            else if ((token.StartsWith('{') || token.StartsWith("~{", StringComparison.Ordinal))
                && token.EndsWith('}'))
            {
                var literalValue = token[(token[0] == '~' ? 2 : 1)..^1];
                isLiteralPlus = literalValue.EndsWith('+');
                if (int.TryParse(literalValue.TrimEnd('+'), System.Globalization.CultureInfo.InvariantCulture, out var size))
                {
                    literalSize = size;
                    isLiteral8 = token[0] == '~';
                }
            }
            else if (token.StartsWith('"') || char.IsDigit(token[0]))
            {
                if (DateTime.TryParse(UnquoteArg(token), System.Globalization.CultureInfo.InvariantCulture, out var dt))
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
                if (int.TryParse(literalValue.TrimEnd('+'), System.Globalization.CultureInfo.InvariantCulture, out var size))
                {
                    literalSize = size;
                    isLiteral8 = braceIdx > 0 && args[braceIdx - 1] == '~';
                }
            }
        }

        return (mailboxName, flags, internalDate, literalSize, isLiteral8, isLiteralPlus);
    }

    // Keep the ordered IMAP protocol handler/parser steps together.
#pragma warning disable MA0051
    private async Task HandleIdleAsync(
#pragma warning restore MA0051
        BoundedLineReader reader, StreamWriter writer, string tag,
        ImapSession session, CancellationTokenSource timeout, int connectionTimeoutSeconds)
    {
        var knownMessages = new List<ImapIdleMessage>();
        long lastKnownModSeq = 0;

        if (session.SelectedFolderId is not null)
        {
            try
            {
                var initial = await GetIdleSnapshotAsync(
                    session.UserId, session.SelectedFolderId.Value, timeout.Token).ConfigureAwait(false);
                ValidateIdleSnapshot(initial);
                knownMessages = initial.Messages;
                lastKnownModSeq = initial.HighestModSeq;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !timeout.IsCancellationRequested)
            {
                LogIdleInitializationUnavailable(logger, exception, session.UserId);
                await writer.WriteLineAsync($"{tag} NO [UNAVAILABLE] IDLE backend unavailable").ConfigureAwait(false);
                return;
            }
        }

        await writer.WriteLineAsync("+ idling").ConfigureAwait(false);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        var backendUnavailableLogged = false;
        var readTask = reader.ReadLineAsync(MaximumCommandLineCharacters, timeout.Token).AsTask();
        while (!timeout.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5), timeout.Token)).ConfigureAwait(false);

            if (completed == readTask)
            {
                var lineResult = await readTask.ConfigureAwait(false);
                if (lineResult.IsTooLong)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                    await writer.WriteLineAsync($"{tag} BAD IDLE terminator is too long").ConfigureAwait(false);
                    return;
                }
                var line = lineResult.Value;
                if (line is null)
                    break;

                if (line.Equals("DONE", StringComparison.OrdinalIgnoreCase))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                    await writer.WriteLineAsync($"{tag} OK IDLE terminated").ConfigureAwait(false);
                    return;
                }

                timeout.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));
                await writer.WriteLineAsync($"{tag} BAD IDLE requires DONE").ConfigureAwait(false);
                return;
            }
            else if (session.SelectedFolderId is not null)
            {
                List<ImapIdleMessage> currentMessages;
                List<string> responseLines;
                long currentModSeq;
                try
                {
                    var snapshot = await GetIdleSnapshotAsync(
                        session.UserId, session.SelectedFolderId.Value, timeout.Token).ConfigureAwait(false);
                    ValidateIdleSnapshot(snapshot);
                    currentMessages = snapshot.Messages;
                    currentModSeq = snapshot.HighestModSeq;
                    responseLines = [];

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
                        responseLines.Add($"* VANISHED {FormatUidRange(vanishedUids)}");
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

                            responseLines.Add($"* {index + 1} EXPUNGE");
                            survivingKnownMessages.RemoveAt(index);
                        }
                    }

                    if (currentMessages.Count != survivingKnownMessages.Count)
                    {
                        responseLines.Add($"* {currentMessages.Count} EXISTS");
                    }

                    if (currentModSeq > lastKnownModSeq)
                    {
                        var changed = currentMessages
                            .Where(email => email.ModSeq > lastKnownModSeq)
                            .ToList();

                        if (changed.Count > 0)
                        {
                            foreach (ref readonly var email in CollectionsMarshal.AsSpan(changed))
                            {
                                var emailId = email.Id;
                                var seqIdx = currentMessages.FindIndex(
                                    candidate => candidate.Id == emailId);
                                if (seqIdx >= 0)
                                {
                                    var seqNum = seqIdx + 1;
                                    var flags = BuildFlagsList(email);
                                    var modSeq = session.CondstoreEnabled
                                        ? $" MODSEQ ({email.ModSeq})"
                                        : string.Empty;
                                    responseLines.Add(
                                        $"* {seqNum} FETCH (FLAGS ({flags}){modSeq})");
                                }
                            }
                        }

                    }

                }
                catch (OperationCanceledException)
                {
                    break;
                }
                // IDLE polling reports a backend failure once and remains available for recovery.
#pragma warning disable CA1031
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    if (!backendUnavailableLogged)
                    {
                        LogIdlePollingUnavailable(logger, exception, session.UserId);
                        backendUnavailableLogged = true;
                    }
                    continue;
                }

                // This protocol loop awaits I/O; a list span cannot cross suspension.
#pragma warning disable HLQ012
                foreach (var responseLine in responseLines)
#pragma warning restore HLQ012
                    await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
                if (currentModSeq > lastKnownModSeq)
                    lastKnownModSeq = currentModSeq;
                knownMessages = currentMessages;
                backendUnavailableLogged = false;
            }
        }
    }

    private async Task<ImapIdleSnapshotResult> GetIdleSnapshotAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var application = scope.ServiceProvider.GetRequiredService<IImapApplicationService>();
        return await application.GetIdleSnapshotAsync(
            new ImapIdleSnapshotRequest(userId, folderId), cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateIdleSnapshot(ImapIdleSnapshotResult snapshot)
    {
        if (snapshot.Messages is null
            || snapshot.HighestModSeq < 0
            || (!snapshot.FolderFound
                && (snapshot.HighestModSeq != 0 || snapshot.Messages.Count != 0))
            || snapshot.Messages.Any(message =>
                message.Id == Guid.Empty || message.Uid < 1 || message.Keywords is null)
            || snapshot.Messages.Select(message => message.Id).Distinct().Count() != snapshot.Messages.Count
            || snapshot.Messages.Select(message => message.Uid).Distinct().Count() != snapshot.Messages.Count)
        {
            throw new InvalidOperationException("The IMAP IDLE snapshot is invalid.");
        }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "No IMAP listeners are enabled.")]
    private static partial void LogNoListeners(ILogger logger);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "IMAP {Mode} listener started on port {Port}")]
    private static partial void LogListenerStarted(ILogger logger, ListenerMode mode, int port);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "IMAP {Mode} listener on port {Port} stopped.")]
    private static partial void LogListenerStopped(ILogger logger, ListenerMode mode, int port);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Rejected IMAP connection from {Endpoint}: connection limit")]
    private static partial void LogConnectionRejected(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "IMAP connection from {Endpoint} timed out")]
    private static partial void LogConnectionTimedOut(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Error handling IMAP connection from {Endpoint}")]
    private static partial void LogConnectionError(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning, Message = "IMAP password authentication service is unavailable")]
    private static partial void LogPasswordAuthenticationUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning, Message = "IMAP OAuth authentication service is unavailable")]
    private static partial void LogOAuthAuthenticationUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "IMAP SASL password authentication service is unavailable")]
    private static partial void LogSaslAuthenticationUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "Mail authentication failed for protocol IMAP from {RemoteIp}")]
    private static partial void LogAuthenticationFailure(ILogger logger, string remoteIp);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "IMAP mailbox listing is unavailable for {UserId}")]
    private static partial void LogMailboxListingUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "IMAP LIST-STATUS is unavailable for {UserId}")]
    private static partial void LogListStatusUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Warning, Message = "IMAP subscribed mailbox listing is unavailable for {UserId}")]
    private static partial void LogSubscribedListingUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning, Message = "IMAP mailbox selection is unavailable for {UserId}")]
    private static partial void LogMailboxSelectionUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Warning, Message = "IMAP mailbox creation is unavailable for {UserId}")]
    private static partial void LogMailboxCreationUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "IMAP mailbox deletion is unavailable for {UserId}")]
    private static partial void LogMailboxDeletionUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Warning, Message = "IMAP mailbox rename is unavailable for {UserId}")]
    private static partial void LogMailboxRenameUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Warning, Message = "IMAP STATUS is unavailable for {UserId}")]
    private static partial void LogStatusUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Warning, Message = "IMAP FETCH unavailable for {UserId}")]
    private static partial void LogFetchUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1019, Level = LogLevel.Warning, Message = "IMAP FETCH seen update unavailable for {UserId}")]
    private static partial void LogFetchSeenUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Warning, Message = "IMAP {Command} is unavailable for {UserId}")]
    private static partial void LogCommandUnavailable(ILogger logger, Exception exception, string command, Guid userId);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Warning, Message = "IMAP SEARCH unavailable for {UserId}")]
    private static partial void LogSearchUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1022, Level = LogLevel.Warning, Message = "IMAP {Operation} is unavailable for {UserId}")]
    private static partial void LogOperationUnavailable(ILogger logger, Exception exception, string operation, Guid userId);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Warning, Message = "IMAP mailbox subscription is unavailable for {UserId}")]
    private static partial void LogSubscriptionUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1024, Level = LogLevel.Debug, Message = "IMAP TLS handshake from {Endpoint} ended before authentication: {ExceptionType}")]
    private static partial void LogTlsPeerEnded(ILogger logger, string endpoint, string exceptionType);

    [LoggerMessage(EventId = 1025, Level = LogLevel.Warning, Message = "IMAP quota lookup is unavailable for {UserId}")]
    private static partial void LogQuotaUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1026, Level = LogLevel.Warning, Message = "IMAP SORT unavailable for {UserId}")]
    private static partial void LogSortUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1027, Level = LogLevel.Warning, Message = "IMAP THREAD unavailable for {UserId}")]
    private static partial void LogThreadUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1028, Level = LogLevel.Warning, Message = "IMAP APPEND preflight unavailable for {UserId}")]
    private static partial void LogAppendPreflightUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1029, Level = LogLevel.Warning, Message = "IMAP APPEND unavailable for {UserId}")]
    private static partial void LogAppendUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Warning, Message = "IMAP IDLE initialization is unavailable for {UserId}")]
    private static partial void LogIdleInitializationUnavailable(ILogger logger, Exception exception, Guid userId);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Warning, Message = "IMAP IDLE polling is unavailable for {UserId}")]
    private static partial void LogIdlePollingUnavailable(ILogger logger, Exception exception, Guid userId);

    /// <summary>
    /// A duplex stream that reads from one underlying stream and writes to another.
    /// Used for COMPRESS=DEFLATE where inflate and deflate are separate streams over the same transport.
    /// </summary>
    private sealed class CompressedDuplexStream(
        Stream transport, Stream readStream, Stream writeStream) : Stream
    {
        private int _disposed;

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
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    writeStream.Dispose();
                }
                finally
                {
                    try
                    {
                        readStream.Dispose();
                    }
                    finally
                    {
                        transport.Dispose();
                    }
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    await writeStream.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await readStream.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await transport.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
