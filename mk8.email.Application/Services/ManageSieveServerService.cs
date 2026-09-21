using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Application.Services;

public sealed class ManageSieveServerService(
    IServiceScopeFactory scopeFactory,
    EnvironmentConfig environment,
    ILogger<ManageSieveServerService> logger) : BackgroundService
{
    private const int MaximumConcurrentConnections = 256;
    private const int MaximumAuthenticationFailures = 5;
    private const int AuthenticatedTimeoutSeconds = 30 * 60;
    private const int MaximumAuthenticationPayloadBytes = 16 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private enum SessionResult
    {
        Closed,
        StartTls,
    }

    private sealed class ManageSieveSession
    {
        public bool IsSecure { get; set; }
        public Guid? UserId { get; set; }
        public string? Username { get; set; }
        public int AuthenticationFailures { get; set; }
        public required string RemoteIp { get; init; }
        public bool IsAuthenticated => UserId is not null;
    }

    private readonly ConnectionLimiter _connectionLimiter = new(MaximumConcurrentConnections);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.Sieve.EnableManageSieve)
        {
            logger.LogWarning("The ManageSieve listener is disabled.");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, environment.Sieve.Port);
        listener.Start();
        logger.LogInformation(
            "ManageSieve listener started on port {Port}",
            environment.Sieve.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleConnectionAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            logger.LogInformation("ManageSieve listener on port {Port} stopped", environment.Sieve.Port);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteEndpoint = client.Client.RemoteEndPoint;
        var remoteAddress = (remoteEndpoint as IPEndPoint)?.Address ?? IPAddress.None;
        var remoteLabel = remoteEndpoint?.ToString() ?? "unknown";
        using var connectionLease = _connectionLimiter.TryAcquire(
            remoteAddress,
            environment.Limits.MaxConnectionsPerIp);
        if (connectionLease is null)
        {
            logger.LogWarning(
                "Rejected ManageSieve connection from {Endpoint}: connection limit",
                remoteLabel);
            client.Dispose();
            return;
        }

        var session = new ManageSieveSession { RemoteIp = remoteAddress.ToString() };
        Stream? stream = null;
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                stream = client.GetStream();
                var sendCapabilities = true;
                while (!timeout.IsCancellationRequested)
                {
                    var result = await RunSessionAsync(
                        stream,
                        session,
                        timeout,
                        sendCapabilities);
                    sendCapabilities = false;
                    if (result != SessionResult.StartTls)
                        break;

                    using var certificate = LoadCertificate();
                    var tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                    if (!await TryAuthenticateAsServerAsync(
                            tlsStream,
                            certificate,
                            timeout.Token,
                            remoteLabel))
                    {
                        await tlsStream.DisposeAsync();
                        return;
                    }
                    stream = tlsStream;
                    session.IsSecure = true;
                    session.UserId = null;
                    session.Username = null;
                    sendCapabilities = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("ManageSieve connection from {Endpoint} timed out", remoteLabel);
        }
        catch (Exception exception) when (
            exception is IOException
                or SocketException
                or AuthenticationException
                or InvalidOperationException)
        {
            logger.LogWarning(exception, "Error handling ManageSieve connection from {Endpoint}", remoteLabel);
        }
        finally
        {
            if (stream is SslStream sslStream)
                await sslStream.DisposeAsync();
        }
    }

    private async Task<SessionResult> RunSessionAsync(
        Stream stream,
        ManageSieveSession session,
        CancellationTokenSource timeout,
        bool sendCapabilities)
    {
        var reader = new ManageSieveWireReader(stream);
        if (sendCapabilities)
            await WriteCapabilitiesAsync(stream, session, timeout.Token);

        while (!timeout.IsCancellationRequested)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(
                session.IsAuthenticated
                    ? Math.Max(environment.Limits.ConnectionTimeoutSeconds, AuthenticatedTimeoutSeconds)
                    : environment.Limits.ConnectionTimeoutSeconds));

            ManageSieveCommand? command;
            try
            {
                command = await reader.ReadCommandAsync(
                    cancellationToken => WriteOkAsync(
                        stream,
                        "Ready for literal data",
                        cancellationToken: cancellationToken),
                    timeout.Token);
            }
            catch (ManageSieveProtocolException exception)
            {
                await WriteStatusAsync(
                    stream,
                    exception.IsFatal ? "BYE" : "NO",
                    exception.Message,
                    exception.ResponseCode,
                    timeout.Token);
                if (exception.IsFatal)
                    return SessionResult.Closed;
                continue;
            }

            if (command is null)
                return SessionResult.Closed;

            try
            {
                var result = await HandleCommandAsync(stream, reader, command, session, timeout);
                if (result is not null)
                    return result.Value;
            }
            catch (DbUpdateException exception)
            {
                logger.LogWarning(
                    exception,
                    "ManageSieve storage command failed for user {UserId}",
                    session.UserId);
                await WriteNoAsync(stream, "The script store is temporarily unavailable.", "TRYLATER", timeout.Token);
            }
        }

        return SessionResult.Closed;
    }

    private async Task<SessionResult?> HandleCommandAsync(
        Stream stream,
        ManageSieveWireReader reader,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationTokenSource timeout)
    {
        var cancellationToken = timeout.Token;
        switch (command.Name)
        {
            case "CAPABILITY":
                if (!HasArgumentCount(command, 0))
                {
                    await WriteNoAsync(stream, "CAPABILITY does not accept arguments.", cancellationToken: cancellationToken);
                    return null;
                }
                await WriteCapabilitiesAsync(stream, session, cancellationToken);
                return null;

            case "STARTTLS":
                if (!HasArgumentCount(command, 0))
                {
                    await WriteNoAsync(stream, "STARTTLS does not accept arguments.", cancellationToken: cancellationToken);
                    return null;
                }
                if (session.IsAuthenticated)
                {
                    await WriteNoAsync(stream, "STARTTLS is only valid before authentication.", cancellationToken: cancellationToken);
                    return null;
                }
                if (session.IsSecure || !environment.Sieve.EnableStartTls)
                {
                    await WriteNoAsync(stream, "STARTTLS is not available.", cancellationToken: cancellationToken);
                    return null;
                }
                await WriteOkAsync(stream, "Begin TLS negotiation", cancellationToken: cancellationToken);
                return SessionResult.StartTls;

            case "AUTHENTICATE":
                await HandleAuthenticateAsync(stream, reader, command, session, timeout);
                return session.AuthenticationFailures >= MaximumAuthenticationFailures
                    ? SessionResult.Closed
                    : null;

            case "LOGOUT":
                if (!HasArgumentCount(command, 0))
                {
                    await WriteNoAsync(stream, "LOGOUT does not accept arguments.", cancellationToken: cancellationToken);
                    return null;
                }
                await WriteOkAsync(stream, "Logout completed", cancellationToken: cancellationToken);
                return SessionResult.Closed;

            case "NOOP":
                await HandleNoopAsync(stream, command, cancellationToken);
                return null;
        }

        if (!session.IsAuthenticated)
        {
            await WriteNoAsync(stream, "Authenticate before using this command.", cancellationToken: cancellationToken);
            return null;
        }

        switch (command.Name)
        {
            case "UNAUTHENTICATE":
                if (!HasArgumentCount(command, 0))
                {
                    await WriteNoAsync(stream, "UNAUTHENTICATE does not accept arguments.", cancellationToken: cancellationToken);
                    return null;
                }
                session.UserId = null;
                session.Username = null;
                await WriteOkAsync(stream, "Unauthenticate completed", cancellationToken: cancellationToken);
                return null;

            case "HAVESPACE":
                await HandleHaveSpaceAsync(stream, command, session, cancellationToken);
                return null;

            case "PUTSCRIPT":
                await HandlePutScriptAsync(stream, command, session, cancellationToken);
                return null;

            case "LISTSCRIPTS":
                await HandleListScriptsAsync(stream, command, session, cancellationToken);
                return null;

            case "SETACTIVE":
                await HandleSetActiveAsync(stream, command, session, cancellationToken);
                return null;

            case "GETSCRIPT":
                await HandleGetScriptAsync(stream, command, session, cancellationToken);
                return null;

            case "DELETESCRIPT":
                await HandleDeleteScriptAsync(stream, command, session, cancellationToken);
                return null;

            case "RENAMESCRIPT":
                await HandleRenameScriptAsync(stream, command, session, cancellationToken);
                return null;

            case "CHECKSCRIPT":
                await HandleCheckScriptAsync(stream, command, cancellationToken);
                return null;

            default:
                await WriteNoAsync(stream, "Unknown command.", cancellationToken: cancellationToken);
                return null;
        }
    }

    private async Task HandleAuthenticateAsync(
        Stream stream,
        ManageSieveWireReader reader,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationTokenSource timeout)
    {
        var cancellationToken = timeout.Token;
        if (session.IsAuthenticated)
        {
            await WriteNoAsync(stream, "The session is already authenticated.", cancellationToken: cancellationToken);
            return;
        }
        if (!session.IsSecure)
        {
            await WriteNoAsync(stream, "Authentication requires TLS.", "ENCRYPT-NEEDED", cancellationToken);
            return;
        }
        if (command.Arguments.Count is < 1 or > 2
            || command.Arguments.Any(argument => argument.Kind != ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "AUTHENTICATE requires a mechanism and optional initial response.", cancellationToken: cancellationToken);
            return;
        }
        if (!string.Equals(command.Arguments[0].Value, "PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await WriteNoAsync(stream, "The requested SASL mechanism is not supported.", cancellationToken: cancellationToken);
            return;
        }

        string? payload;
        if (command.Arguments.Count == 2)
        {
            payload = command.Arguments[1].Value;
        }
        else
        {
            await WriteLineAsync(stream, "\"\"", cancellationToken);
            try
            {
                payload = await reader.ReadSaslResponseAsync(
                    continuationToken => WriteOkAsync(
                        stream,
                        "Ready for literal data",
                        cancellationToken: continuationToken),
                    cancellationToken);
            }
            catch (ManageSieveProtocolException exception)
            {
                if (exception.IsFatal)
                {
                    session.AuthenticationFailures = MaximumAuthenticationFailures;
                    await WriteStatusAsync(
                        stream,
                        "BYE",
                        exception.Message,
                        exception.ResponseCode,
                        cancellationToken);
                    return;
                }
                await RecordAuthenticationFailureAsync(stream, session, exception.Message, cancellationToken);
                return;
            }
        }

        if (payload is null || payload == "*")
        {
            await WriteNoAsync(stream, "Authentication was cancelled.", cancellationToken: cancellationToken);
            return;
        }

        if (!TryDecodePlainCredentials(payload, out var authorizationIdentity, out var username, out var password)
            || authorizationIdentity.Length > 0
                && !string.Equals(authorizationIdentity, username, StringComparison.OrdinalIgnoreCase))
        {
            await RecordAuthenticationFailureAsync(stream, session, "Authentication failed.", cancellationToken);
            return;
        }

        AuthenticatedMailUser? authenticated;
        using (var scope = scopeFactory.CreateScope())
        {
            var authenticator = scope.ServiceProvider.GetRequiredService<IMailAuthenticator>();
            authenticated = await authenticator.AuthenticateAsync(username, password, cancellationToken);
        }
        if (authenticated is null)
        {
            await RecordAuthenticationFailureAsync(stream, session, "Authentication failed.", cancellationToken);
            return;
        }

        session.UserId = authenticated.Id;
        session.Username = authenticated.Username;
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(environment.Limits.ConnectionTimeoutSeconds, AuthenticatedTimeoutSeconds)));
        await WriteOkAsync(stream, "Authentication successful", cancellationToken: cancellationToken);
    }

    private async Task RecordAuthenticationFailureAsync(
        Stream stream,
        ManageSieveSession session,
        string message,
        CancellationToken cancellationToken)
    {
        session.AuthenticationFailures++;
        logger.LogWarning(
            "Mail authentication failed for protocol ManageSieve from {RemoteIp}",
            session.RemoteIp);
        if (session.AuthenticationFailures >= MaximumAuthenticationFailures)
        {
            await WriteStatusAsync(
                stream,
                "BYE",
                "Too many failed authentication attempts",
                cancellationToken: cancellationToken);
            return;
        }
        await WriteNoAsync(stream, message, cancellationToken: cancellationToken);
    }

    private async Task HandleHaveSpaceAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String, ManageSieveTokenKind.Number)
            || !long.TryParse(command.Arguments[1].Value, out var size)
            || size < 0)
        {
            await WriteNoAsync(stream, "HAVESPACE requires a script name and non-negative size.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var result = await scripts.CheckSpaceAsync(
            session.UserId!.Value,
            command.Arguments[0].Value,
            size,
            environment.Sieve.MaxScriptsPerUser,
            cancellationToken);
        await WriteOperationResultAsync(stream, result, "Space is available", cancellationToken);
    }

    private async Task HandlePutScriptAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String, ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "PUTSCRIPT requires a script name and content.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var result = await scripts.PutAsync(
            session.UserId!.Value,
            command.Arguments[0].Value,
            command.Arguments[1].Value,
            environment.Sieve.MaxScriptsPerUser,
            cancellationToken);
        await WriteOperationResultAsync(stream, result, "Script stored", cancellationToken);
    }

    private async Task HandleListScriptsAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasArgumentCount(command, 0))
        {
            await WriteNoAsync(stream, "LISTSCRIPTS does not accept arguments.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        foreach (var script in await scripts.ListAsync(session.UserId!.Value, cancellationToken))
        {
            await WriteLineAsync(
                stream,
                Quote(script.Name) + (script.IsActive ? " ACTIVE" : string.Empty),
                cancellationToken);
        }
        await WriteOkAsync(stream, "Scripts listed", cancellationToken: cancellationToken);
    }

    private async Task HandleSetActiveAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "SETACTIVE requires a script name.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var name = command.Arguments[0].Value;
        var result = await scripts.SetActiveAsync(
            session.UserId!.Value,
            name.Length == 0 ? null : name,
            cancellationToken);
        await WriteOperationResultAsync(stream, result, "Active script updated", cancellationToken);
    }

    private async Task HandleGetScriptAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "GETSCRIPT requires a script name.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var script = await scripts.GetAsync(
            session.UserId!.Value,
            command.Arguments[0].Value,
            cancellationToken);
        if (script is null)
        {
            await WriteNoAsync(stream, "The script does not exist.", "NONEXISTENT", cancellationToken);
            return;
        }

        var content = StrictUtf8.GetBytes(script.Content);
        await WriteLineAsync(stream, $"{{{content.Length}}}", cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
        await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        await WriteOkAsync(stream, "Script returned", cancellationToken: cancellationToken);
    }

    private async Task HandleDeleteScriptAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "DELETESCRIPT requires a script name.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var result = await scripts.DeleteAsync(
            session.UserId!.Value,
            command.Arguments[0].Value,
            cancellationToken);
        await WriteOperationResultAsync(stream, result, "Script deleted", cancellationToken);
    }

    private async Task HandleRenameScriptAsync(
        Stream stream,
        ManageSieveCommand command,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String, ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "RENAMESCRIPT requires old and new script names.", cancellationToken: cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var result = await scripts.RenameAsync(
            session.UserId!.Value,
            command.Arguments[0].Value,
            command.Arguments[1].Value,
            cancellationToken);
        await WriteOperationResultAsync(stream, result, "Script renamed", cancellationToken);
    }

    private async Task HandleCheckScriptAsync(
        Stream stream,
        ManageSieveCommand command,
        CancellationToken cancellationToken)
    {
        if (!HasKinds(command, ManageSieveTokenKind.String))
        {
            await WriteNoAsync(stream, "CHECKSCRIPT requires script content.", cancellationToken: cancellationToken);
            return;
        }
        if (StrictUtf8.GetByteCount(command.Arguments[0].Value) > SieveScript.MaximumScriptBytes)
        {
            await WriteNoAsync(stream, "The script exceeds the one-megabyte limit.", "QUOTA/MAXSIZE", cancellationToken);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var scripts = scope.ServiceProvider.GetRequiredService<ISieveScriptService>();
        var compilation = scripts.Validate(command.Arguments[0].Value);
        if (!compilation.Succeeded)
        {
            var diagnostic = compilation.Diagnostics[0];
            await WriteNoAsync(
                stream,
                $"Line {diagnostic.Line}, column {diagnostic.Column}: {diagnostic.Message}",
                cancellationToken: cancellationToken);
            return;
        }
        await WriteOkAsync(stream, "Script is valid", cancellationToken: cancellationToken);
    }

    private static async Task HandleNoopAsync(
        Stream stream,
        ManageSieveCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Arguments.Count > 1
            || command.Arguments.Count == 1
                && command.Arguments[0].Kind != ManageSieveTokenKind.String)
        {
            await WriteNoAsync(stream, "NOOP accepts at most one string argument.", cancellationToken: cancellationToken);
            return;
        }
        var responseCode = command.Arguments.Count == 1
            ? command.Arguments[0].Value
            : null;
        if (responseCode is null || IsQuotedStringSafe(responseCode))
        {
            await WriteOkAsync(
                stream,
                "NOOP completed",
                responseCode is null ? null : $"TAG {Quote(responseCode)}",
                cancellationToken);
            return;
        }

        var tagBytes = StrictUtf8.GetBytes(responseCode);
        await WriteRawAsync(stream, StrictUtf8.GetBytes($"OK (TAG {{{tagBytes.Length}}}\r\n"), cancellationToken);
        await WriteRawAsync(stream, tagBytes, cancellationToken);
        await WriteRawAsync(stream, StrictUtf8.GetBytes(") \"NOOP completed\"\r\n"), cancellationToken);
    }

    private async Task WriteCapabilitiesAsync(
        Stream stream,
        ManageSieveSession session,
        CancellationToken cancellationToken)
    {
        await WriteCapabilityAsync(stream, "IMPLEMENTATION", "mk8.email ManageSieve", cancellationToken);
        await WriteCapabilityAsync(stream, "VERSION", "1.0", cancellationToken);
        await WriteCapabilityAsync(
            stream,
            "SASL",
            session.IsSecure ? "PLAIN" : string.Empty,
            cancellationToken);
        await WriteCapabilityAsync(
            stream,
            "SIEVE",
            string.Join(' ', SieveScript.SupportedCapabilities.Order(StringComparer.Ordinal)),
            cancellationToken);
        if (!session.IsSecure && !session.IsAuthenticated && environment.Sieve.EnableStartTls)
            await WriteLineAsync(stream, Quote("STARTTLS"), cancellationToken);
        await WriteCapabilityAsync(
            stream,
            "MAXREDIRECTS",
            SieveScript.MaximumRedirects.ToString(),
            cancellationToken);
        await WriteCapabilityAsync(stream, "LANGUAGE", "i-default", cancellationToken);
        await WriteLineAsync(stream, Quote("UNAUTHENTICATE"), cancellationToken);
        if (session.Username is not null)
            await WriteCapabilityAsync(stream, "OWNER", session.Username, cancellationToken);
        await WriteOkAsync(stream, "Capability completed", cancellationToken: cancellationToken);
    }

    private static Task WriteCapabilityAsync(
        Stream stream,
        string name,
        string value,
        CancellationToken cancellationToken) =>
        WriteLineAsync(stream, $"{Quote(name)} {Quote(value)}", cancellationToken);

    private static Task WriteOperationResultAsync(
        Stream stream,
        SieveScriptOperationResult result,
        string successMessage,
        CancellationToken cancellationToken) =>
        result.Succeeded
            ? WriteOkAsync(stream, successMessage, cancellationToken: cancellationToken)
            : WriteNoAsync(
                stream,
                result.Error ?? "The operation failed.",
                result.ResponseCode,
                cancellationToken);

    private static Task WriteOkAsync(
        Stream stream,
        string message,
        string? responseCode = null,
        CancellationToken cancellationToken = default) =>
        WriteStatusAsync(stream, "OK", message, responseCode, cancellationToken);

    private static Task WriteNoAsync(
        Stream stream,
        string message,
        string? responseCode = null,
        CancellationToken cancellationToken = default) =>
        WriteStatusAsync(stream, "NO", message, responseCode, cancellationToken);

    private static Task WriteStatusAsync(
        Stream stream,
        string status,
        string message,
        string? responseCode = null,
        CancellationToken cancellationToken = default)
    {
        var code = responseCode is null ? string.Empty : $" ({responseCode})";
        return WriteLineAsync(
            stream,
            $"{status}{code} {Quote(SanitizeResponseText(message))}",
            cancellationToken);
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string line,
        CancellationToken cancellationToken)
    {
        if (line.Contains('\r') || line.Contains('\n') || line.Contains('\0'))
        {
            throw new InvalidOperationException("ManageSieve response lines cannot contain control delimiters.");
        }
        var bytes = StrictUtf8.GetBytes(line + "\r\n");
        await WriteRawAsync(stream, bytes, cancellationToken);
    }

    private static async Task WriteRawAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string Quote(string value) =>
        '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static bool IsQuotedStringSafe(string value) =>
        !value.Any(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029');

    private static string SanitizeResponseText(string value)
    {
        if (IsQuotedStringSafe(value))
            return value;
        var sanitized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            sanitized.Append(
                char.IsControl(character) || character is '\u2028' or '\u2029'
                    ? '\uFFFD'
                    : character);
        }
        return sanitized.ToString();
    }

    private static bool HasArgumentCount(ManageSieveCommand command, int count) =>
        command.Arguments.Count == count;

    private static bool HasKinds(
        ManageSieveCommand command,
        params ManageSieveTokenKind[] kinds) =>
        command.Arguments.Count == kinds.Length
        && command.Arguments.Select(argument => argument.Kind).SequenceEqual(kinds);

    private static bool TryDecodePlainCredentials(
        string payload,
        out string authorizationIdentity,
        out string username,
        out string password)
    {
        authorizationIdentity = string.Empty;
        username = string.Empty;
        password = string.Empty;
        if (payload.Length > MaximumAuthenticationPayloadBytes)
            return false;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }
        if (decoded.Length > MaximumAuthenticationPayloadBytes)
            return false;

        var firstSeparator = Array.IndexOf(decoded, (byte)0);
        var secondSeparator = firstSeparator < 0
            ? -1
            : Array.IndexOf(decoded, (byte)0, firstSeparator + 1);
        if (firstSeparator < 0
            || secondSeparator <= firstSeparator + 1
            || Array.IndexOf(decoded, (byte)0, secondSeparator + 1) >= 0)
        {
            return false;
        }

        try
        {
            authorizationIdentity = StrictUtf8.GetString(decoded, 0, firstSeparator);
            username = StrictUtf8.GetString(
                decoded,
                firstSeparator + 1,
                secondSeparator - firstSeparator - 1);
            password = StrictUtf8.GetString(decoded, secondSeparator + 1, decoded.Length - secondSeparator - 1);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        return username.Length > 0 && password.Length > 0;
    }

    private X509Certificate2 LoadCertificate()
    {
        if (environment.Tls.CertificateKeyPath is not null)
        {
            return X509Certificate2.CreateFromPemFile(
                environment.Tls.CertificatePath!,
                environment.Tls.CertificateKeyPath);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(
            environment.Tls.CertificatePath!,
            password: null);
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
                "ManageSieve TLS handshake from {Endpoint} ended before authentication: {ExceptionType}",
                remoteLabel,
                exception.GetType().Name);
            return false;
        }
    }
}
