using System.Text;

namespace mk8.email.Application.Protocol;

public static class OAuthSasl
{
    private const int MaximumEncodedResponseLength = 8192;
    private const int MaximumUsernameLength = 320;
    private const int MaximumTokenLength = 4096;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryParseXOAuth2(
        string encodedResponse,
        out string username,
        out string accessToken)
    {
        username = string.Empty;
        accessToken = string.Empty;
        if (encodedResponse.Length is 0 or > MaximumEncodedResponseLength)
            return false;

        string response;
        try
        {
            response = StrictUtf8.GetString(Convert.FromBase64String(encodedResponse));
        }
        catch (Exception exception) when (
            exception is FormatException or DecoderFallbackException)
        {
            return false;
        }

        const string userPrefix = "user=";
        const string bearerPrefix = "auth=Bearer ";
        var fields = response.Split('\x01');
        if (fields.Length != 4
            || fields[2].Length != 0
            || fields[3].Length != 0
            || !fields[0].StartsWith(userPrefix, StringComparison.Ordinal)
            || !fields[1].StartsWith(bearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        username = fields[0][userPrefix.Length..];
        accessToken = fields[1][bearerPrefix.Length..];
        return username.Length is > 0 and <= MaximumUsernameLength
            && accessToken.Length is > 0 and <= MaximumTokenLength
            && !username.ContainsAny(['\r', '\n', '\0'])
            && !accessToken.Any(char.IsControl)
            && !accessToken.Any(char.IsWhiteSpace);
    }
}
