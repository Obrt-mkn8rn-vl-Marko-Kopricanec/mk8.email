using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal sealed class GatewayWebPushService : IDisposable
{
    private readonly HttpClient _client;
    private readonly IGatewayTrafficJournal _journal;
    private readonly int _maximumJournalPayloadBytes;

    public GatewayWebPushService(
        IGatewayTrafficJournal journal,
        EnvironmentConfig environment)
        : this(CreateHandler(), journal, environment.Messaging.MaxPayloadBytes)
    {
    }

    internal GatewayWebPushService(
        HttpMessageHandler handler,
        IGatewayTrafficJournal journal,
        int maximumJournalPayloadBytes)
    {
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _journal = journal;
        _maximumJournalPayloadBytes = maximumJournalPayloadBytes;
    }

    private static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = ConnectPublicAsync,
        };
    }

    public async Task<bool> IsSafeUrlAsync(
        string value,
        CancellationToken cancellationToken)
    {
        if (!TryParseUrl(value, out var uri))
            return false;
        try
        {
            var addresses = await ResolvePublicAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            return addresses.Count > 0;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return false;
        }
    }

    public async Task<WebPushSendResult> SendAsync(
        WebPushSendRequest subscription,
        Guid sessionId,
        Guid exchangeId,
        CancellationToken cancellationToken)
    {
        if (!TryParseUrl(subscription.Url, out var uri))
            return new WebPushSendResult(WebPushSendOutcome.Failed);
        if (subscription.ExpiresAt <= DateTimeOffset.UtcNow)
            return new WebPushSendResult(WebPushSendOutcome.Gone);
        if (subscription.Payload is null || subscription.Payload.Length == 0)
            return new WebPushSendResult(WebPushSendOutcome.Failed);
        var body = subscription.Payload;
        var encrypted = false;
        if (subscription.P256dh is not null || subscription.Auth is not null)
        {
            try
            {
                if (subscription.P256dh is null || subscription.Auth is null)
                    return new WebPushSendResult(WebPushSendOutcome.Failed);
                body = JmapPushEncryption.Encrypt(
                    subscription.Payload,
                    subscription.P256dh,
                    subscription.Auth);
                encrypted = true;
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                return new WebPushSendResult(WebPushSendOutcome.Failed);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation(
            "TTL",
            Math.Clamp((long)(subscription.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 0, 86_400)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (encrypted)
            request.Content.Headers.ContentEncoding.Add("aes128gcm");

        var requestTrace = JsonSerializer.SerializeToUtf8Bytes(new
        {
            method = "POST",
            url = subscription.Url,
            headers = request.Headers
                .Concat(request.Content.Headers)
                .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.Ordinal),
            bodyBase64 = Convert.ToBase64String(body),
        });
        if (requestTrace.Length > _maximumJournalPayloadBytes)
            throw new InvalidOperationException("The Web Push request exceeds the journal payload limit.");
        await AppendExternalAsync(
            sessionId,
            exchangeId,
            sequence: 1,
            GatewayTrafficDirections.Outbound,
            requestTrace).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return await RecordNetworkFailureAsync(sessionId, exchangeId).ConfigureAwait(false);
        }

        using (response)
        {
            byte[] responseBody;
            bool truncated;
            try
            {
                (responseBody, truncated) = await ReadResponseBodyAsync(
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                return await RecordNetworkFailureAsync(sessionId, exchangeId).ConfigureAwait(false);
            }

            var responseTrace = JsonSerializer.SerializeToUtf8Bytes(new
            {
                status = (int)response.StatusCode,
                headers = response.Headers
                    .Concat(response.Content.Headers)
                    .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.Ordinal),
                bodyBase64 = Convert.ToBase64String(responseBody),
                truncated,
            });
            await AppendExternalAsync(
                sessionId,
                exchangeId,
                sequence: 2,
                GatewayTrafficDirections.Inbound,
                responseTrace).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return new WebPushSendResult(WebPushSendOutcome.Success);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new WebPushSendResult(WebPushSendOutcome.Gone);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new WebPushSendResult(WebPushSendOutcome.RateLimited);
            return new WebPushSendResult(WebPushSendOutcome.Failed);
        }
    }

    private async Task<WebPushSendResult> RecordNetworkFailureAsync(
        Guid sessionId,
        Guid exchangeId)
    {
        await AppendExternalAsync(
            sessionId,
            exchangeId,
            sequence: 2,
            GatewayTrafficDirections.Inbound,
            JsonSerializer.SerializeToUtf8Bytes(new { error = "network-failure" })).ConfigureAwait(false);
        return new WebPushSendResult(WebPushSendOutcome.Failed);
    }

    private async Task<(byte[] Body, bool Truncated)> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var maximumBodyBytes = Math.Max(0L, ((long)_maximumJournalPayloadBytes - 32_768) * 3 / 4);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var streamLifetime = stream.ConfigureAwait(false);
        var copy = new MemoryStream();
        await using var copyLifetime = copy.ConfigureAwait(false);
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return (copy.ToArray(), false);
            var remaining = maximumBodyBytes - copy.Length;
            if (read > remaining)
            {
                if (remaining > 0)
                    await copy.WriteAsync(buffer.AsMemory(0, checked((int)remaining)), cancellationToken).ConfigureAwait(false);
                return (copy.ToArray(), true);
            }
            await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AppendExternalAsync(
        Guid sessionId,
        Guid exchangeId,
        long sequence,
        string direction,
        byte[] payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _journal.AppendAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                sequence,
                direction,
                WebPushPresentationOperations.Protocol,
                "application/json",
                payload,
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UtcNow,
                exchangeId),
            timeout.Token).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private static bool TryParseUrl(string value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.Port is > 0 and <= 65535;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            Socket? socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                return stream;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                lastError = exception;
                if (exception is OperationCanceledException)
                    throw;
            }
            finally
            {
                socket?.Dispose();
            }
        }
        throw new HttpRequestException("The public push endpoint could not be reached.", lastError);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = [literal];
        else
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new SocketException((int)SocketError.HostNotFound);
        return addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.IsIPv6SiteLocal
            && (bytes[0] & 0xfe) != 0xfc
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
}

internal static class JmapPushEncryption
{
    public static bool TryValidateKeys(string p256dh, string auth)
    {
        try
        {
            var publicKey = DecodeBase64Url(p256dh);
            var authSecret = DecodeBase64Url(auth);
            if (publicKey.Length != 65 || publicKey[0] != 4 || authSecret.Length != 16)
                return false;
            using var peer = CreatePeer(publicKey);
            _ = peer.ExportParameters(false);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        string receiverPublicKey,
        string authenticationSecret)
    {
        var receiverBytes = DecodeBase64Url(receiverPublicKey);
        var auth = DecodeBase64Url(authenticationSecret);
        if (receiverBytes.Length != 65 || receiverBytes[0] != 4 || auth.Length != 16)
            throw new CryptographicException("Invalid Web Push receiver keys.");

        using var receiver = CreatePeer(receiverBytes);
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sharedSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);
        var senderParameters = sender.ExportParameters(false);
        var senderPublic = new byte[65];
        senderPublic[0] = 4;
        senderParameters.Q.X!.CopyTo(senderPublic, 1);
        senderParameters.Q.Y!.CopyTo(senderPublic, 33);

        var keyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
        var keyInfo = new byte[keyInfoPrefix.Length + receiverBytes.Length + senderPublic.Length];
        keyInfoPrefix.CopyTo(keyInfo, 0);
        receiverBytes.CopyTo(keyInfo, keyInfoPrefix.Length);
        senderPublic.CopyTo(keyInfo, keyInfoPrefix.Length + receiverBytes.Length);
        var inputKeyMaterial = Expand(Extract(auth, sharedSecret), keyInfo, 32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var pseudoRandomKey = Extract(salt, inputKeyMaterial);
        var contentEncryptionKey = Expand(
            pseudoRandomKey,
            Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"),
            16);
        var nonce = Expand(
            pseudoRandomKey,
            Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"),
            12);

        var recordPlaintext = new byte[plaintext.Length + 1];
        plaintext.CopyTo(recordPlaintext);
        recordPlaintext[^1] = 2;
        var ciphertext = new byte[recordPlaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(contentEncryptionKey, 16))
            aes.Encrypt(nonce, recordPlaintext, ciphertext, tag);

        var recordSize = checked((uint)Math.Max(4096, ciphertext.Length + tag.Length + 1));
        var result = new byte[16 + 4 + 1 + senderPublic.Length + ciphertext.Length + tag.Length];
        salt.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), recordSize);
        result[20] = checked((byte)senderPublic.Length);
        senderPublic.CopyTo(result, 21);
        ciphertext.CopyTo(result, 21 + senderPublic.Length);
        tag.CopyTo(result, 21 + senderPublic.Length + ciphertext.Length);
        return result;
    }

    private static ECDiffieHellman CreatePeer(byte[] publicKey) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey.AsSpan(1, 32).ToArray(),
                Y = publicKey.AsSpan(33, 32).ToArray(),
            },
        });

    private static byte[] Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> input)
    {
        using var hmac = new HMACSHA256(salt.ToArray());
        return hmac.ComputeHash(input.ToArray());
    }

    private static byte[] Expand(ReadOnlySpan<byte> key, ReadOnlySpan<byte> info, int length)
    {
        using var hmac = new HMACSHA256(key.ToArray());
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var written = 0;
        byte counter = 1;
        while (written < length)
        {
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(previous.Length));
            input[^1] = counter++;
            previous = hmac.ComputeHash(input);
            var copy = Math.Min(previous.Length, length - written);
            previous.AsSpan(0, copy).CopyTo(output.AsSpan(written));
            written += copy;
        }
        return output;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
            throw new FormatException("Invalid base64url value.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(padded);
    }
}
