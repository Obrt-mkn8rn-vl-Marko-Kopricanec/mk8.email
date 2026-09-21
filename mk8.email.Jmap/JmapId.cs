using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace mk8.email.Jmap;

public static class JmapId
{
    public static string Account(Guid inboxId) => FormatGuid('A', inboxId);
    public static string Mailbox(Guid folderId) => FormatGuid('M', folderId);
    public static string Email(Guid emailId) => FormatGuid('E', emailId);
    public static string RawBlob(Guid emailId) => FormatGuid('B', emailId);
    public static string UploadedBlob(Guid blobId) => FormatGuid('U', blobId);
    public static string Identity(Guid inboxId) => FormatGuid('I', inboxId);
    public static string Submission(Guid submissionId) => FormatGuid('S', submissionId);
    public static string PushSubscription(Guid subscriptionId) => FormatGuid('P', subscriptionId);
    public static string AddressBook(Guid collectionId) => FormatGuid('D', collectionId);
    public static string ContactCard(Guid resourceId) => FormatGuid('C', resourceId);

    public static string Thread(string storedThreadId) =>
        "T" + NormalizeOpaqueId(storedThreadId);

    public static string BodyPartBlob(Guid emailId, string partId)
    {
        var encodedPart = Base64UrlEncode(Encoding.UTF8.GetBytes(partId));
        var pathId = $"R{emailId:N}_{encodedPart}";
        if (IsValidId(pathId))
            return pathId;

        var digest = Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(partId)));
        var nestingDepth = partId.Count(character => character == '!');
        return $"H{emailId:N}_{nestingDepth}_{digest}";
    }

    public static bool TryParseAccount(string? value, out Guid id) => TryParseGuid(value, 'A', out id);
    public static bool TryParseMailbox(string? value, out Guid id) => TryParseGuid(value, 'M', out id);
    public static bool TryParseEmail(string? value, out Guid id) => TryParseGuid(value, 'E', out id);
    public static bool TryParseRawBlob(string? value, out Guid id) => TryParseGuid(value, 'B', out id);
    public static bool TryParseUploadedBlob(string? value, out Guid id) => TryParseGuid(value, 'U', out id);
    public static bool TryParseIdentity(string? value, out Guid id) => TryParseGuid(value, 'I', out id);
    public static bool TryParseSubmission(string? value, out Guid id) => TryParseGuid(value, 'S', out id);
    public static bool TryParsePushSubscription(string? value, out Guid id) => TryParseGuid(value, 'P', out id);
    public static bool TryParseAddressBook(string? value, out Guid id) => TryParseGuid(value, 'D', out id);
    public static bool TryParseContactCard(string? value, out Guid id) => TryParseGuid(value, 'C', out id);

    public static bool TryParseThread(string? value, out string storedThreadId)
    {
        storedThreadId = string.Empty;
        if (string.IsNullOrEmpty(value) || value[0] != 'T' || !IsValidId(value))
            return false;

        storedThreadId = value[1..];
        return storedThreadId.Length > 0;
    }

    public static bool TryParseBodyPartBlob(
        string? value,
        out Guid emailId,
        out string partId)
    {
        emailId = Guid.Empty;
        partId = string.Empty;
        if (string.IsNullOrEmpty(value) || value[0] != 'R' || !IsValidId(value))
            return false;

        var separator = value.IndexOf('_');
        if (separator != 33
            || !Guid.TryParseExact(value.AsSpan(1, 32), "N", out emailId)
            || !TryBase64UrlDecode(value[(separator + 1)..], out var bytes))
        {
            emailId = Guid.Empty;
            return false;
        }

        try
        {
            partId = new UTF8Encoding(false, true).GetString(bytes);
            return partId.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            emailId = Guid.Empty;
            partId = string.Empty;
            return false;
        }
    }

    public static bool TryParseHashedBodyPartBlob(
        string? value,
        out Guid emailId,
        out int nestingDepth,
        out byte[] pathHash)
    {
        emailId = Guid.Empty;
        nestingDepth = 0;
        pathHash = [];
        if (string.IsNullOrEmpty(value) || value[0] != 'H' || !IsValidId(value))
            return false;

        var firstSeparator = value.IndexOf('_');
        var secondSeparator = value.IndexOf('_', firstSeparator + 1);
        if (firstSeparator != 33
            || secondSeparator <= firstSeparator + 1
            || !Guid.TryParseExact(value.AsSpan(1, 32), "N", out emailId)
            || !int.TryParse(
                value.AsSpan(firstSeparator + 1, secondSeparator - firstSeparator - 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out nestingDepth)
            || !TryBase64UrlDecode(value[(secondSeparator + 1)..], out pathHash)
            || pathHash.Length != SHA256.HashSizeInBytes)
        {
            emailId = Guid.Empty;
            nestingDepth = 0;
            pathHash = [];
            return false;
        }
        return true;
    }

    public static bool IsValidId(string value)
    {
        if (value.Length is < 1 or > 255)
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                return false;
        }

        return true;
    }

    private static string FormatGuid(char prefix, Guid value) => $"{prefix}{value:N}";

    private static bool TryParseGuid(string? value, char prefix, out Guid id)
    {
        id = Guid.Empty;
        return value is { Length: 33 }
            && value[0] == prefix
            && Guid.TryParseExact(value.AsSpan(1), "N", out id);
    }

    private static string NormalizeOpaqueId(string value)
    {
        if (!string.IsNullOrEmpty(value)
            && value.Length < 255
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return value;
        }

        return Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
    {
        var output = new byte[Base64.GetMaxEncodedToUtf8Length(value.Length)];
        Base64.EncodeToUtf8(value, output, out _, out var written);
        return Encoding.ASCII.GetString(output, 0, written)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => "!",
        };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
