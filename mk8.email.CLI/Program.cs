using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Enums;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Contracts.Storage;
using mk8.email.Hosting;
using mk8.email.Messaging;
using Npgsql;

return await RunManagementCommandAsync(args);

static async Task<int> RunManagementCommandAsync(string[] arguments)
{
    if (!IsSupportedCommand(arguments))
    {
        WriteUsage();
        return 2;
    }

    if (arguments.Length == 4
        && arguments[0] == "--create-account"
        && !Enum.TryParse<UserRole>(arguments[2], ignoreCase: true, out _))
    {
        Console.Error.WriteLine("The account role is not valid.");
        return 2;
    }

    if (arguments.Length == 3
        && arguments[0] == "--set-domain-active"
        && !bool.TryParse(arguments[2], out _))
    {
        Console.Error.WriteLine("The domain state must be true or false.");
        return 2;
    }

    if (arguments.Length == 3
        && arguments[0] == "--revoke-app-password"
        && !Guid.TryParse(arguments[2], out _))
    {
        Console.Error.WriteLine("The application password identifier is not valid.");
        return 2;
    }

    if (arguments.Length == 3
        && arguments[0] == "--purge-quarantined-smoke-message"
        && (arguments[2].Length != 32
            || arguments[2].Any(character => character is not
                (>= '0' and <= '9' or >= 'a' and <= 'f'))))
    {
        Console.Error.WriteLine("The smoke marker is not valid.");
        return 2;
    }

    try
    {
        var isDevelopment = arguments.Contains("--dev", StringComparer.Ordinal)
            || arguments.Contains("--development", StringComparer.Ordinal)
            || Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development";
        if (arguments.Length == 2
            && arguments[0] is ("--validate-gateway-config" or "--validate-worker-config"
                or "--healthcheck-gateway"
                or "--probe-gateway-backends" or "--probe-worker-backends"
                or "--probe-worker-dispatch"))
        {
            var role = arguments[0] is ("--validate-gateway-config" or "--healthcheck-gateway"
                or "--probe-gateway-backends")
                ? EnvironmentValidationRole.Gateway
                : EnvironmentValidationRole.ApplicationWorker;
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, role);
            if (!validated.Messaging.Enabled)
                throw new InvalidOperationException("Distributed messaging must be enabled.");
            if (arguments[0] == "--healthcheck-gateway")
            {
                using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await GatewayListenerHealthCheck.IsHealthyAsync(validated, healthTimeout.Token)
                    ? 0
                    : 1;
            }
            if (arguments[0] == "--probe-worker-dispatch")
            {
                await using var probeServices = new ServiceCollection()
                    .AddDistributedMessaging(validated)
                    .BuildServiceProvider();
                await DistributedApplicationProbe.ProbeAsync(
                    probeServices.GetRequiredService<IApplicationRequestClient>(),
                    TimeSpan.FromSeconds(10));
                Console.WriteLine("The Application Worker completed a distributed request.");
                return 0;
            }
            if (arguments[0].StartsWith("--probe-", StringComparison.Ordinal))
            {
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var source = NpgsqlDataSource.Create(validated.BuildConnectionString());
                if (role == EnvironmentValidationRole.Gateway)
                    await GatewayDatabasePrivilegeProbe.ProbeAsync(source, probeTimeout.Token);
                await using var objects = new ServiceCollection()
                    .AddAzureBlobObjectStorage(validated)
                    .BuildServiceProvider();
                await DistributedBackendProbe.ProbeAsync(
                    source,
                    objects.GetRequiredService<ILargeObjectStore>(),
                    probeTimeout.Token);
                Console.WriteLine($"The {role} distributed backends are reachable.");
                return 0;
            }
            Console.WriteLine($"The {role} configuration is valid.");
            return 0;
        }
        if (arguments.Length == 2 && arguments[0] == "--audit-blob-references")
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            if (!validated.Messaging.Enabled)
                throw new InvalidOperationException("Distributed messaging must be enabled.");
            await using var dataSource = NpgsqlDataSource.Create(validated.BuildConnectionString());
            await using var services = new ServiceCollection()
                .AddAzureBlobObjectStorage(validated)
                .BuildServiceProvider();
            var count = await DistributedBlobReferenceAudit.AuditAsync(
                dataSource, services.GetRequiredService<ILargeObjectStore>());
            Console.WriteLine($"Verified {count} database-referenced Azure Blob object(s).");
            return 0;
        }
        if (arguments.Length == 3 && arguments[0] == "--export-distributed-snapshot")
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            if (!validated.Messaging.Enabled)
                throw new InvalidOperationException("Distributed messaging must be enabled.");
            await using var dataSource = NpgsqlDataSource.Create(validated.BuildConnectionString());
            await using var services = new ServiceCollection()
                .AddAzureBlobObjectStorage(validated)
                .BuildServiceProvider();
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                timeout.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                var result = await DistributedBackupExporter.ExportAsync(
                    dataSource,
                    services.GetRequiredService<ILargeObjectStore>(),
                    validated.BuildConnectionString(),
                    arguments[2],
                    cancellationToken: timeout.Token);
                Console.WriteLine(
                    $"Exported {result.ReferenceCount} references and "
                    + $"{result.UniqueContentCount} unique Blob contents. "
                    + "Restore and ETag rebinding are not yet verified.");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        if (arguments.Length == 3 && arguments[0] == "--purge-quarantined-smoke-message")
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            using var host = BuildHost(arguments, validated, includeLargeObjects: true);
            using var scope = host.Services.CreateScope();
            var removed = await scope.ServiceProvider
                .GetRequiredService<MailQueueMaintenanceService>()
                .PurgeQuarantinedSmokeMessageAsync(arguments[2]);
            Console.WriteLine(removed
                ? "The quarantined smoke message and Blob were removed."
                : "No unique quarantined smoke message matched the marker.");
            return removed ? 0 : 1;
        }
        var environmentConfig = EnvironmentLoader.Load(isDevelopment);

        if (arguments.SequenceEqual(["--healthcheck"]))
        {
            using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await GatewayListenerHealthCheck.IsHealthyAsync(environmentConfig, healthTimeout.Token) ? 0 : 1;
        }

        if (arguments.SequenceEqual(["--initialize-empty-database"]))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IDatabaseInitializationService>()
                .InitializeEmptyDatabaseAsync();
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.SequenceEqual(["--ensure-runtime-schema"]))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<MailRuntimeSchemaService>()
                .EnsureAsync();
            Console.WriteLine("The native mail runtime schema is ready.");
            return 0;
        }

        if (arguments.Length == 3 && arguments[0] == "--ensure-domain")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .EnsureDomainAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 4 && arguments[0] == "--create-account")
        {
            var passwordPath = Path.GetFullPath(arguments[3]);
            if (!File.Exists(passwordPath))
            {
                Console.Error.WriteLine("The password file does not exist.");
                return 2;
            }

            var password = File.ReadAllText(passwordPath).TrimEnd('\r', '\n');
            _ = Enum.TryParse<UserRole>(arguments[2], ignoreCase: true, out var role);
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .CreateAccountAsync(arguments[1], password, role);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--set-catchall")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .SetCatchAllAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--set-domain-active")
        {
            _ = bool.TryParse(arguments[2], out var isActive);
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .SetDomainActiveAsync(arguments[1], isActive);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--create-app-password")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IApplicationPasswordService>()
                .CreateAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;

            Console.WriteLine($"ID: {result.Id:D}");
            Console.WriteLine($"Password: {result.Password}");
            return 0;
        }

        if (arguments.Length == 2 && arguments[0] == "--list-app-passwords")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var passwords = await scope.ServiceProvider
                .GetRequiredService<IApplicationPasswordService>()
                .ListAsync(arguments[1]);
            foreach (var password in passwords)
            {
                Console.WriteLine(string.Join('\t',
                    password.Id.ToString("D"),
                    password.Name,
                    password.CreatedAt.ToString("O"),
                    password.LastUsedAt?.ToString("O") ?? "never",
                    password.RevokedAt?.ToString("O") ?? "active"));
            }
            return 0;
        }

        if (arguments.Length == 3 && arguments[0] == "--revoke-app-password")
        {
            _ = Guid.TryParse(arguments[2], out var applicationPasswordId);
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var revoked = await scope.ServiceProvider
                .GetRequiredService<IApplicationPasswordService>()
                .RevokeAsync(arguments[1], applicationPasswordId);
            Console.WriteLine(revoked
                ? "The application password is revoked."
                : "The application password was not found.");
            return revoked ? 0 : 1;
        }

        if (arguments.Length == 3 && arguments[0] == "--enroll-totp")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .BeginTotpEnrollmentAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;
            Console.WriteLine($"Secret: {result.Secret}");
            Console.WriteLine($"URI: {result.ProvisioningUri}");
            return 0;
        }

        if (arguments.Length == 3 && arguments[0] == "--confirm-totp")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .ConfirmTotpEnrollmentAsync(arguments[1], arguments[2]);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;
            foreach (var recoveryCode in result.RecoveryCodes ?? [])
                Console.WriteLine($"Recovery: {recoveryCode}");
            return 0;
        }

        if (arguments.Length == 2 && arguments[0] == "--totp-status")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var status = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .GetStatusAsync(arguments[1]);
            Console.WriteLine(status.IsEnrolled ? "TOTP MFA is enabled." : "TOTP MFA is not enabled.");
            if (status.Name is not null)
            {
                Console.WriteLine($"Name: {status.Name}");
                Console.WriteLine($"Created: {status.CreatedAt:O}");
                Console.WriteLine($"Verified: {status.VerifiedAt:O}");
                Console.WriteLine($"Last used: {status.LastUsedAt:O}");
                Console.WriteLine($"Recovery codes: {status.RemainingRecoveryCodes}");
            }
            return status.IsEnrolled ? 0 : 1;
        }

        if (arguments.Length == 2 && arguments[0] == "--regenerate-recovery-codes")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .RegenerateRecoveryCodesAsync(arguments[1]);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;
            foreach (var recoveryCode in result.RecoveryCodes ?? [])
                Console.WriteLine($"Recovery: {recoveryCode}");
            return 0;
        }

        if (arguments.Length == 2 && arguments[0] == "--disable-totp")
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var disabled = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .DisableTotpAsync(arguments[1]);
            Console.WriteLine(disabled
                ? "TOTP MFA is disabled and OAuth device grants are revoked."
                : "The account does not have a TOTP enrollment.");
            return disabled ? 0 : 1;
        }

        throw new InvalidOperationException("The management command is not supported.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"The management command failed: {exception.GetBaseException().Message}");
        return 1;
    }
}

static bool IsSupportedCommand(string[] arguments) =>
    arguments.SequenceEqual(["--healthcheck"])
    || arguments.SequenceEqual(["--initialize-empty-database"])
    || arguments.SequenceEqual(["--ensure-runtime-schema"])
    || arguments.Length == 3 && arguments[0] == "--ensure-domain"
    || arguments.Length == 4 && arguments[0] == "--create-account"
    || arguments.Length == 3 && arguments[0] == "--set-catchall"
    || arguments.Length == 3 && arguments[0] == "--set-domain-active"
    || arguments.Length == 3 && arguments[0] == "--create-app-password"
    || arguments.Length == 2 && arguments[0] == "--list-app-passwords"
    || arguments.Length == 3 && arguments[0] == "--revoke-app-password"
    || arguments.Length == 3 && arguments[0] == "--purge-quarantined-smoke-message"
    || arguments.Length == 3 && arguments[0] == "--export-distributed-snapshot"
    || arguments.Length == 3 && arguments[0] == "--enroll-totp"
    || arguments.Length == 3 && arguments[0] == "--confirm-totp"
    || arguments.Length == 2 && arguments[0] == "--totp-status"
    || arguments.Length == 2 && arguments[0] == "--regenerate-recovery-codes"
    || arguments.Length == 2 && arguments[0] == "--disable-totp"
    || arguments.Length == 2 && arguments[0] is
        ("--validate-gateway-config" or "--validate-worker-config"
            or "--healthcheck-gateway"
            or "--probe-gateway-backends" or "--probe-worker-backends"
            or "--probe-worker-dispatch" or "--audit-blob-references");

static void WriteUsage()
{
    Console.Error.WriteLine("Use one valid management command.");
}

static IHost BuildHost(
    string[] arguments,
    EnvironmentConfig environmentConfig,
    bool includeLargeObjects = false)
{
    var builder = Host.CreateApplicationBuilder(arguments);
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
    builder.Services.AddInfrastructure(environmentConfig);
    if (includeLargeObjects)
        builder.Services.AddAzureBlobObjectStorage(environmentConfig);
    builder.Services.AddApplication();
    return builder.Build();
}
