using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Environment;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class MfaServiceTests
{
    private const string Username = "mfa.user@example.com";

    [TestMethod]
    public void TotpMatchesRfc6238Sha1VectorAndBase32Encoding()
    {
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(59).UtcDateTime;

        Assert.AreEqual("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpMfa.EncodeSecret(secret));
        Assert.AreEqual("287082", TotpMfa.ComputeCode(secret, timestamp));
        Assert.IsTrue(TotpMfa.TryDecodeSecret(TotpMfa.EncodeSecret(secret), out var decoded));
        CollectionAssert.AreEqual(secret, decoded);
    }

    [TestMethod]
    public async Task TotpEnrollmentEncryptsSecretAndIssuesOneTimeRecoveryCodes()
    {
        await using var database = CreateDatabase();
        var service = new MfaService(database, CreateEnvironment());

        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Primary authenticator");

        Assert.IsTrue(enrollment.Succeeded);
        Assert.IsNotNull(enrollment.Secret);
        StringAssert.StartsWith(enrollment.ProvisioningUri, "otpauth://totp/");
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret, out var secret));
        Assert.AreEqual(20, secret.Length);
        var stored = await database.MfaTotpCredentials.SingleAsync();
        Assert.AreEqual(20, stored.EncryptedSecret.Length);
        Assert.AreEqual(12, stored.EncryptionNonce.Length);
        Assert.AreEqual(16, stored.EncryptionTag.Length);
        Assert.IsFalse(stored.EncryptedSecret.SequenceEqual(secret));

        var confirmation = await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow));

        Assert.IsTrue(confirmation.Succeeded);
        Assert.HasCount(5, confirmation.RecoveryCodes!);
        var recoveryCodes = confirmation.RecoveryCodes!;
        Assert.AreEqual(5, recoveryCodes.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(recoveryCodes.All(code => code.StartsWith("mk8_rc_", StringComparison.Ordinal)));
        Assert.AreEqual(5, await database.MfaRecoveryCodes.CountAsync());
        Assert.IsTrue(await database.MfaRecoveryCodes.AllAsync(code => code.CodeHash.Length == 32));
        Assert.IsNotNull(stored.VerifiedAt);
        Assert.IsTrue((await service.GetStatusAsync(Username)).IsEnrolled);

        var recoveryCode = recoveryCodes[0];
        Assert.AreEqual(
            MfaVerificationResult.Succeeded,
            await service.VerifyForAuthenticationAsync(stored.UserId, recoveryCode));
        Assert.AreEqual(
            MfaVerificationResult.Failed,
            await service.VerifyForAuthenticationAsync(stored.UserId, recoveryCode));
        Assert.AreEqual(4, (await service.GetStatusAsync(Username)).RemainingRecoveryCodes);
    }

    [TestMethod]
    public async Task TotpCodesCannotBeReplayed()
    {
        await using var database = CreateDatabase();
        var service = new MfaService(database, CreateEnvironment());
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone");
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
        var code = TotpMfa.ComputeCode(secret, DateTime.UtcNow);
        Assert.IsTrue((await service.ConfirmTotpEnrollmentAsync(Username, code)).Succeeded);
        var credential = await database.MfaTotpCredentials.SingleAsync();
        credential.LastAcceptedTimeStep = null;
        await database.SaveChangesAsync();

        Assert.AreEqual(
            MfaVerificationResult.Succeeded,
            await service.VerifyForAuthenticationAsync(credential.UserId, code));
        Assert.AreEqual(
            MfaVerificationResult.Failed,
            await service.VerifyForAuthenticationAsync(credential.UserId, code));
    }

    [TestMethod]
    public async Task ActiveMfaBlocksPrimaryProtocolPasswordButKeepsApplicationPasswordFallback()
    {
        await using var database = CreateDatabase();
        var environment = CreateEnvironment();
        var applicationPassword = await new ApplicationPasswordService(database)
            .CreateAsync(Username, "Legacy Thunderbird");
        var service = new MfaService(database, environment);
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone");
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
        Assert.IsTrue((await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow))).Succeeded);

        var authenticator = new MailAuthenticator(database);

        Assert.IsNull(await authenticator.AuthenticateAsync(Username, "primary-password"));
        Assert.IsNotNull(await authenticator.AuthenticatePrimaryAsync(Username, "primary-password"));
        Assert.IsNotNull(await authenticator.AuthenticateAsync(Username, applicationPassword.Password!));
    }

    [TestMethod]
    public async Task EnablingOrDisablingMfaRevokesOAuthDeviceCredentials()
    {
        await using var database = CreateDatabase();
        var environment = CreateEnvironment();
        var tokenService = new OAuthTokenService(database, environment);
        var service = new MfaService(database, environment);
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var original = await tokenService.CreateGrantAsync(
            userId,
            "thunderbird",
            "Existing device",
            ["offline_access", "imap"]);
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone");
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));

        var confirmation = await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow));

        Assert.IsTrue(confirmation.Succeeded);
        Assert.IsNull(await tokenService.AuthenticateAccessTokenAsync(original!.AccessToken, "imap"));
        var afterEnrollment = await tokenService.CreateGrantAsync(
            userId,
            "thunderbird",
            "New device",
            ["offline_access", "imap"]);

        Assert.IsTrue(await service.DisableTotpAsync(Username));
        Assert.IsNull(await tokenService.AuthenticateAccessTokenAsync(afterEnrollment!.AccessToken, "imap"));
        Assert.IsFalse((await service.GetStatusAsync(Username)).IsEnrolled);
    }

    [TestMethod]
    public async Task RecoveryCodeRegenerationInvalidatesEveryPriorCode()
    {
        await using var database = CreateDatabase();
        var environment = CreateEnvironment();
        var service = new MfaService(database, environment);
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone");
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
        var confirmation = await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow));
        var oldCode = confirmation.RecoveryCodes![0];

        var regenerated = await service.RegenerateRecoveryCodesAsync(Username);

        Assert.IsTrue(regenerated.Succeeded);
        var regeneratedCodes = regenerated.RecoveryCodes!;
        CollectionAssert.DoesNotContain(regeneratedCodes.ToArray(), oldCode);
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        Assert.AreEqual(
            MfaVerificationResult.Failed,
            await service.VerifyForAuthenticationAsync(userId, oldCode));
        Assert.AreEqual(
            MfaVerificationResult.Succeeded,
            await service.VerifyForAuthenticationAsync(userId, regeneratedCodes[0]));
    }

    [TestMethod]
    public async Task UnenrolledAccountDoesNotRequireASecondFactor()
    {
        await using var database = CreateDatabase();
        var userId = await database.Users.Select(user => user.Id).SingleAsync();
        var service = new MfaService(database, CreateEnvironment());

        Assert.AreEqual(
            MfaVerificationResult.NotRequired,
            await service.VerifyForAuthenticationAsync(userId, string.Empty));
    }

    private static EnvironmentConfig CreateEnvironment() => new()
    {
        OAuth = new OAuthConfig
        {
            EnableOAuth = true,
            AccessTokenMinutes = 10,
            RefreshTokenDays = 90,
        },
        Mfa = new MfaConfig
        {
            EnableTotp = true,
            Issuer = "mk8.email test",
            EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            RecoveryCodeCount = 5,
        },
    };

    private static EmailDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase($"mfa-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var database = new EmailDbContext(options);
        var company = new CompanyDB
        {
            Id = Guid.CreateVersion7(),
            Name = "MFA Test",
            IsActive = true,
        };
        database.Addresses.Add(new AddressDB
        {
            Id = Guid.CreateVersion7(),
            Domain = "example.com",
            Company = company,
            IsActive = true,
        });
        database.Users.Add(new UserDB
        {
            Id = Guid.CreateVersion7(),
            Username = Username,
            PasswordHash = PasswordHasher.Hash("primary-password"),
            Company = company,
            IsActive = true,
        });
        database.SaveChanges();
        return database;
    }
}
