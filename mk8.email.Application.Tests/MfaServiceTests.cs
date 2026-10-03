using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Infrastructure.Models;
using mk8.email.Utils;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class MfaServiceTests
{
    private const string Username = "mfa.user@example.com";

    [TestMethod]
    public void TotpMatchesRfc6238Sha1VectorAndBase32Encoding()
    {
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(59).UtcDateTime;

        Assert.AreEqual("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpMfa.EncodeSecret(secret), StringComparer.Ordinal);
        Assert.AreEqual("287082", TotpMfa.ComputeCode(secret, timestamp), StringComparer.Ordinal);
        Assert.IsTrue(TotpMfa.TryDecodeSecret(TotpMfa.EncodeSecret(secret), out var decoded));
        CollectionAssert.AreEqual(secret, decoded);
    }

    [TestMethod]
    public async Task TotpEnrollmentEncryptsSecretAndIssuesOneTimeRecoveryCodes()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var service = new MfaService(database, CreateEnvironment());

        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Primary authenticator").ConfigureAwait(false);

        Assert.IsTrue(enrollment.Succeeded);
        Assert.IsNotNull(enrollment.Secret);
        StringAssert.StartsWith(enrollment.ProvisioningUri, "otpauth://totp/", StringComparison.Ordinal);
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret, out var secret));
        Assert.AreEqual(20, secret.Length);
        var stored = await database.MfaTotpCredentials.SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(20, stored.EncryptedSecret.Length);
        Assert.AreEqual(12, stored.EncryptionNonce.Length);
        Assert.AreEqual(16, stored.EncryptionTag.Length);
        Assert.IsFalse(stored.EncryptedSecret.SequenceEqual(secret));

        var confirmation = await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow)).ConfigureAwait(false);

        Assert.IsTrue(confirmation.Succeeded);
        Assert.HasCount(5, confirmation.RecoveryCodes!);
        var recoveryCodes = confirmation.RecoveryCodes!;
        Assert.AreEqual(5, recoveryCodes.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(recoveryCodes.All(code => code.StartsWith("mk8_rc_", StringComparison.Ordinal)));
        Assert.AreEqual(5, await database.MfaRecoveryCodes.CountAsync().ConfigureAwait(false));
        Assert.IsTrue(await database.MfaRecoveryCodes.AllAsync(code => code.CodeHash.Length == 32).ConfigureAwait(false));
        Assert.IsNotNull(stored.VerifiedAt);
        Assert.IsTrue((await service.GetStatusAsync(Username).ConfigureAwait(false)).IsEnrolled);

        var recoveryCode = recoveryCodes[0];
        Assert.AreEqual(
            MfaVerificationResult.Succeeded,
            await service.VerifyForAuthenticationAsync(stored.UserId, recoveryCode).ConfigureAwait(false));
        Assert.AreEqual(
            MfaVerificationResult.Failed,
            await service.VerifyForAuthenticationAsync(stored.UserId, recoveryCode).ConfigureAwait(false));
        Assert.AreEqual(4, (await service.GetStatusAsync(Username).ConfigureAwait(false)).RemainingRecoveryCodes);
    }

    [TestMethod]
    public async Task TotpCodesCannotBeReplayed()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var service = new MfaService(database, CreateEnvironment());
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone").ConfigureAwait(false);
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
        var code = TotpMfa.ComputeCode(secret, DateTime.UtcNow);
        Assert.IsTrue((await service.ConfirmTotpEnrollmentAsync(Username, code).ConfigureAwait(false)).Succeeded);
        var credential = await database.MfaTotpCredentials.SingleAsync().ConfigureAwait(false);
        credential.LastAcceptedTimeStep = null;
        await database.SaveChangesAsync().ConfigureAwait(false);

        Assert.AreEqual(
            MfaVerificationResult.Succeeded,
            await service.VerifyForAuthenticationAsync(credential.UserId, code).ConfigureAwait(false));
        Assert.AreEqual(
            MfaVerificationResult.Failed,
            await service.VerifyForAuthenticationAsync(credential.UserId, code).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ActiveMfaBlocksPrimaryProtocolPasswordButKeepsApplicationPasswordFallback()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var environment = CreateEnvironment();
        var applicationPassword = await new ApplicationPasswordService(database)
            .CreateAsync(Username, "Legacy Thunderbird").ConfigureAwait(false);
        var service = new MfaService(database, environment);
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone").ConfigureAwait(false);
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
        Assert.IsTrue((await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow)).ConfigureAwait(false)).Succeeded);

        var authenticator = new MailAuthenticator(database);

        Assert.IsNull(await authenticator.AuthenticateAsync(Username, "primary-password").ConfigureAwait(false));
        Assert.IsNotNull(await authenticator.AuthenticatePrimaryAsync(Username, "primary-password").ConfigureAwait(false));
        Assert.IsNotNull(await authenticator.AuthenticateAsync(Username, applicationPassword.Password!).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task EnablingOrDisablingMfaRevokesOAuthDeviceCredentials()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var environment = CreateEnvironment();
        var tokenService = new OAuthTokenService(database, environment);
        var service = new MfaService(database, environment);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var original = await tokenService.CreateGrantAsync(
            userId,
            "thunderbird",
            "Existing device",
            ["offline_access", "imap"]).ConfigureAwait(false);
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone").ConfigureAwait(false);
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));

        var confirmation = await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow)).ConfigureAwait(false);

        Assert.IsTrue(confirmation.Succeeded);
        Assert.IsNull(await tokenService.AuthenticateAccessTokenAsync(original!.AccessToken, "imap").ConfigureAwait(false));
        var afterEnrollment = await tokenService.CreateGrantAsync(
            userId,
            "thunderbird",
            "New device",
            ["offline_access", "imap"]).ConfigureAwait(false);

        Assert.IsTrue(await service.DisableTotpAsync(Username).ConfigureAwait(false));
        Assert.IsNull(await tokenService.AuthenticateAccessTokenAsync(afterEnrollment!.AccessToken, "imap").ConfigureAwait(false));
        Assert.IsFalse((await service.GetStatusAsync(Username).ConfigureAwait(false)).IsEnrolled);
    }

    [TestMethod]
    public async Task RecoveryCodeRegenerationInvalidatesEveryPriorCode()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var environment = CreateEnvironment();
        var service = new MfaService(database, environment);
        var enrollment = await service.BeginTotpEnrollmentAsync(Username, "Phone").ConfigureAwait(false);
        Assert.IsTrue(TotpMfa.TryDecodeSecret(enrollment.Secret!, out var secret));
        var confirmation = await service.ConfirmTotpEnrollmentAsync(
            Username,
            TotpMfa.ComputeCode(secret, DateTime.UtcNow)).ConfigureAwait(false);
        var oldCode = confirmation.RecoveryCodes![0];

        var regenerated = await service.RegenerateRecoveryCodesAsync(Username).ConfigureAwait(false);

        Assert.IsTrue(regenerated.Succeeded);
        var regeneratedCodes = regenerated.RecoveryCodes!;
        CollectionAssert.DoesNotContain(regeneratedCodes.ToArray(), oldCode);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        Assert.AreEqual(
            MfaVerificationResult.Failed,
            await service.VerifyForAuthenticationAsync(userId, oldCode).ConfigureAwait(false));
        Assert.AreEqual(
            MfaVerificationResult.Succeeded,
            await service.VerifyForAuthenticationAsync(userId, regeneratedCodes[0]).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task UnenrolledAccountDoesNotRequireASecondFactor()
    {
        var database = CreateDatabase();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var userId = await database.Users.Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var service = new MfaService(database, CreateEnvironment());

        Assert.AreEqual(
            MfaVerificationResult.NotRequired,
            await service.VerifyForAuthenticationAsync(userId, string.Empty).ConfigureAwait(false));
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
