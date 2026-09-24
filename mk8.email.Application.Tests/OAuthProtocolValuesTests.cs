using System.Text;
using mk8.email.Application.Protocol;
using mk8.email.Contracts.Protocol;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class OAuthProtocolValuesTests
{
    [TestMethod]
    public void XOAuth2ParserAcceptsThunderbirdPayloadAndRejectsAmbiguousForms()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "user=person@example.com\u0001auth=Bearer mk8_at_token\u0001\u0001"));

        Assert.IsTrue(OAuthSasl.TryParseXOAuth2(encoded, out var username, out var token));
        Assert.AreEqual("person@example.com", username);
        Assert.AreEqual("mk8_at_token", token);

        Assert.IsFalse(OAuthSasl.TryParseXOAuth2("not-base64!", out _, out _));
        Assert.IsFalse(OAuthSasl.TryParseXOAuth2(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "user=person@example.com\u0001auth=Bearer token\u0001injected=value\u0001")),
            out _,
            out _));
        Assert.IsFalse(OAuthSasl.TryParseXOAuth2(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "user=person@example.com\u0001auth=Bearer token with-space\u0001\u0001")),
            out _,
            out _));
    }

    [TestMethod]
    public void OAuthRequestValuesRequirePkceOfflineAccessAndLoopbackRedirects()
    {
        var verifier = new string('a', 64);
        var challenge = OAuthProtocolValues.CreatePkceChallenge(verifier);

        Assert.IsTrue(OAuthProtocolValues.IsValidPkceVerifier(verifier));
        Assert.IsTrue(OAuthProtocolValues.IsValidPkceChallenge(challenge));
        Assert.IsFalse(OAuthProtocolValues.IsValidPkceVerifier(null!));
        Assert.IsFalse(OAuthProtocolValues.IsValidPkceChallenge(null!));
        Assert.IsTrue(OAuthProtocolValues.IsAllowedRedirectUri("http://127.0.0.1:49152/"));
        Assert.IsTrue(OAuthProtocolValues.IsAllowedRedirectUri("http://[::1]:49152/"));
        Assert.IsFalse(OAuthProtocolValues.IsAllowedRedirectUri("https://attacker.example/"));
        Assert.IsFalse(OAuthProtocolValues.IsAllowedRedirectUri("http://127.0.0.1/callback"));
        Assert.IsTrue(OAuthProtocolValues.TryNormalizeScopes(
            ["smtp offline_access imap smtp"],
            out var normalized));
        CollectionAssert.AreEquivalent(
            new[] { "imap", "offline_access", "smtp" },
            normalized);
        Assert.IsFalse(OAuthProtocolValues.TryNormalizeScopes(["imap"], out _));
        Assert.IsTrue(OAuthProtocolValues.TryNormalizeScopes(
            ["openid offline_access imap"],
            out var identityScopes));
        CollectionAssert.AreEquivalent(
            new[] { "imap", "offline_access", "openid" },
            identityScopes);
    }
}
