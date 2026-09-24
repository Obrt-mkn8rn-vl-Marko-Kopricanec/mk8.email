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
using Azure;
using Azure.Storage.Blobs;
using Npgsql;

return await RunManagementCommandAsync(args).ConfigureAwait(false);

// The deployed command router preserves established argument order, secret handling, and exit codes.
// Extracting command families requires a separate process-level compatibility sweep.
#pragma warning disable MA0051
static async Task<int> RunManagementCommandAsync(string[] arguments)
{
    if (!IsSupportedCommand(arguments))
    {
        WriteUsage();
        return 2;
    }

    if (Matches(arguments, 4, "--create-account")
        && !Enum.TryParse<UserRole>(arguments[2], ignoreCase: true, out _))
    {
        await Console.Error.WriteLineAsync("The account role is not valid.").ConfigureAwait(false);
        return 2;
    }

    if (Matches(arguments, 3, "--set-domain-active")
        && !bool.TryParse(arguments[2], out _))
    {
        await Console.Error.WriteLineAsync("The domain state must be true or false.").ConfigureAwait(false);
        return 2;
    }

    if (Matches(arguments, 3, "--revoke-app-password")
        && !Guid.TryParse(arguments[2], out _))
    {
        await Console.Error.WriteLineAsync(
            "The application password identifier is not valid.").ConfigureAwait(false);
        return 2;
    }

    if (Matches(arguments, 3, "--purge-quarantined-smoke-message")
        && (arguments[2].Length != 32
            || arguments[2].Any(character => character is not
                (>= '0' and <= '9' or >= 'a' and <= 'f'))))
    {
        await Console.Error.WriteLineAsync("The smoke marker is not valid.").ConfigureAwait(false);
        return 2;
    }

    try
    {
        var isDevelopment = arguments.Contains("--dev", StringComparer.Ordinal)
            || arguments.Contains("--development", StringComparer.Ordinal)
            || string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                "Development",
                StringComparison.Ordinal);
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
            if (string.Equals(arguments[0], "--healthcheck-gateway", StringComparison.Ordinal))
            {
                using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await GatewayListenerHealthCheck.IsHealthyAsync(validated, healthTimeout.Token).ConfigureAwait(false)
                    ? 0
                    : 1;
            }
            if (string.Equals(arguments[0], "--probe-worker-dispatch", StringComparison.Ordinal))
            {
                var source = NpgsqlDataSource.Create(validated.BuildConnectionString());
                await using var sourceLifetime = source.ConfigureAwait(false);
                await DistributedRestoreActivationGuard.RequireReadyAsync(source).ConfigureAwait(false);
                var probeServices = new ServiceCollection()
                    .AddDistributedMessaging(validated)
                    .BuildServiceProvider();
                await using var probeServicesLifetime = probeServices.ConfigureAwait(false);
                await DistributedApplicationProbe.ProbeAsync(
                    probeServices.GetRequiredService<IApplicationRequestClient>(),
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Console.WriteLine("The Application Worker completed a distributed request.");
                return 0;
            }
            if (arguments[0].StartsWith("--probe-", StringComparison.Ordinal))
            {
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var source = NpgsqlDataSource.Create(validated.BuildConnectionString());
                await using var sourceLifetime = source.ConfigureAwait(false);
                await DistributedRestoreActivationGuard.RequireReadyAsync(
                    source, probeTimeout.Token).ConfigureAwait(false);
                if (role == EnvironmentValidationRole.Gateway)
                    await GatewayDatabasePrivilegeProbe.ProbeAsync(source, probeTimeout.Token).ConfigureAwait(false);
                var objects = new ServiceCollection()
                    .AddAzureBlobObjectStorage(validated)
                    .BuildServiceProvider();
                await using var objectsLifetime = objects.ConfigureAwait(false);
                await DistributedBackendProbe.ProbeAsync(
                    source,
                    objects.GetRequiredService<ILargeObjectStore>(),
                    probeTimeout.Token).ConfigureAwait(false);
                Console.WriteLine($"The {role} distributed backends are reachable.");
                return 0;
            }
            Console.WriteLine($"The {role} configuration is valid.");
            return 0;
        }
        if (Matches(arguments, 2, "--audit-blob-references"))
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            if (!validated.Messaging.Enabled)
                throw new InvalidOperationException("Distributed messaging must be enabled.");
            var dataSource = NpgsqlDataSource.Create(validated.BuildConnectionString());
            await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
            var services = new ServiceCollection()
                .AddAzureBlobObjectStorage(validated)
                .BuildServiceProvider();
            await using var servicesLifetime = services.ConfigureAwait(false);
            var count = await DistributedBlobReferenceAudit.AuditAsync(
                dataSource, services.GetRequiredService<ILargeObjectStore>()).ConfigureAwait(false);
            Console.WriteLine($"Verified {count} database-referenced Azure Blob object(s).");
            return 0;
        }
        if (Matches(arguments, 2, "--verify-distributed-snapshot"))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                timeout.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                var summary = await DistributedBackupRestorer.VerifyAsync(
                    arguments[1], timeout.Token).ConfigureAwait(false);
                Console.WriteLine(
                    $"Verified distributed snapshot v{summary.SchemaVersion}: "
                    + $"{summary.ReferenceCount} references, "
                    + $"{summary.UniqueContentCount} unique Blob contents. "
                    + "A target restore and Blob audit are still required.");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        if (arguments.Length == 6
            && arguments[0] is ("--publish-distributed-archive" or "--fetch-distributed-archive"))
        {
            var connectionString = DistributedArchiveStore.ReadPrivateConnectionString(
                arguments[1]);
            var service = new BlobServiceClient(connectionString);
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(4));
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                timeout.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                if (string.Equals(arguments[0], "--publish-distributed-archive", StringComparison.Ordinal))
                {
                    await DistributedArchiveStore.PublishAsync(
                        service, arguments[2], arguments[3], arguments[4],
                        arguments[5], timeout.Token).ConfigureAwait(false);
                    Console.WriteLine($"Published encrypted archive {arguments[3]} to Azure Blob storage.");
                }
                else
                {
                    await DistributedArchiveStore.FetchAsync(
                        service, arguments[2], arguments[3], arguments[4],
                        arguments[5], timeout.Token).ConfigureAwait(false);
                    Console.WriteLine($"Fetched and verified encrypted archive {arguments[3]}.");
                }
                return 0;
            }
            catch (RequestFailedException exception)
            {
                await Console.Error.WriteLineAsync(
                    $"Azure Blob archive transport failed: HTTP {exception.Status} ({exception.ErrorCode}).")
                    .ConfigureAwait(false);
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        if (Matches(arguments, 3, "--export-distributed-snapshot"))
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            if (!validated.Messaging.Enabled)
                throw new InvalidOperationException("Distributed messaging must be enabled.");
            var dataSource = NpgsqlDataSource.Create(validated.BuildConnectionString());
            await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
            var services = new ServiceCollection()
                .AddAzureBlobObjectStorage(validated)
                .BuildServiceProvider();
            await using var servicesLifetime = services.ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                timeout.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                var pgDumpExecutable = Environment.GetEnvironmentVariable(
                    "MK8EMAIL_PG_DUMP_EXECUTABLE");
                if (!string.IsNullOrWhiteSpace(pgDumpExecutable)
                    && (!Path.IsPathFullyQualified(pgDumpExecutable)
                        || !File.Exists(pgDumpExecutable)))
                {
                    throw new InvalidOperationException(
                        "MK8EMAIL_PG_DUMP_EXECUTABLE must name an absolute existing file.");
                }
                var result = await DistributedBackupExporter.ExportAsync(
                    dataSource,
                    services.GetRequiredService<ILargeObjectStore>(),
                    validated.BuildConnectionString(),
                    arguments[2],
                    pgDumpExecutable: pgDumpExecutable ?? "pg_dump",
                    cancellationToken: timeout.Token).ConfigureAwait(false);
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
        if (Matches(arguments, 3, "--restore-distributed-snapshot"))
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            if (!validated.Messaging.Enabled)
                throw new InvalidOperationException("Distributed messaging must be enabled.");
            var dataSource = NpgsqlDataSource.Create(validated.BuildConnectionString());
            await using var dataSourceLifetime = dataSource.ConfigureAwait(false);
            var services = new ServiceCollection()
                .AddAzureBlobObjectStorage(validated)
                .BuildServiceProvider();
            await using var servicesLifetime = services.ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                timeout.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                var result = await DistributedBackupRestorer.RestoreAsync(
                    arguments[2],
                    dataSource,
                    validated.BuildConnectionString(),
                    services.GetRequiredService<ILargeObjectStore>(),
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                Console.WriteLine(
                    $"Restored {result.ReferenceCount} database references and "
                    + $"{result.ImportedObjectCount} unique Blob objects. "
                    + "Keep services stopped until the restore smoke and backend audit pass.");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        if (Matches(arguments, 3, "--purge-quarantined-smoke-message"))
        {
            var validated = EnvironmentLoader.LoadFromFile(
                arguments[1], isDevelopment, EnvironmentValidationRole.ApplicationWorker);
            using var host = BuildHost(arguments, validated, includeLargeObjects: true);
            using var scope = host.Services.CreateScope();
            var removed = await scope.ServiceProvider
                .GetRequiredService<MailQueueMaintenanceService>()
                .PurgeQuarantinedSmokeMessageAsync(arguments[2]).ConfigureAwait(false);
            Console.WriteLine(removed
                ? "The quarantined smoke message and Blob were removed."
                : "No unique quarantined smoke message matched the marker.");
            return removed ? 0 : 1;
        }
        var environmentConfig = EnvironmentLoader.Load(isDevelopment);

        if (arguments.SequenceEqual(["--healthcheck"]))
        {
            using var healthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await GatewayListenerHealthCheck.IsHealthyAsync(environmentConfig, healthTimeout.Token).ConfigureAwait(false) ? 0 : 1;
        }

        if (arguments.SequenceEqual(["--initialize-empty-database"]))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IDatabaseInitializationService>()
                .InitializeEmptyDatabaseAsync().ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (arguments.SequenceEqual(["--ensure-runtime-schema"]))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<MailRuntimeSchemaService>()
                .EnsureAsync().ConfigureAwait(false);
            Console.WriteLine("The native mail runtime schema is ready.");
            return 0;
        }

        if (Matches(arguments, 3, "--ensure-domain"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .EnsureDomainAsync(arguments[1], arguments[2]).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (Matches(arguments, 4, "--create-account"))
        {
            var passwordPath = Path.GetFullPath(arguments[3]);
            if (!File.Exists(passwordPath))
            {
                await Console.Error.WriteLineAsync("The password file does not exist.").ConfigureAwait(false);
                return 2;
            }

            var password = (await File.ReadAllTextAsync(passwordPath).ConfigureAwait(false))
                .TrimEnd('\r', '\n');
            _ = Enum.TryParse<UserRole>(arguments[2], ignoreCase: true, out var role);
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .CreateAccountAsync(arguments[1], password, role).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (Matches(arguments, 3, "--set-catchall"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .SetCatchAllAsync(arguments[1], arguments[2]).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (Matches(arguments, 3, "--set-domain-active"))
        {
            _ = bool.TryParse(arguments[2], out var isActive);
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IMailAdministrationService>()
                .SetDomainActiveAsync(arguments[1], isActive).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Succeeded ? 0 : 1;
        }

        if (Matches(arguments, 3, "--create-app-password"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<IApplicationPasswordService>()
                .CreateAsync(arguments[1], arguments[2]).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;

            Console.WriteLine($"ID: {result.Id:D}");
            Console.WriteLine($"Password: {result.Password}");
            return 0;
        }

        if (Matches(arguments, 2, "--list-app-passwords"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var passwords = await scope.ServiceProvider
                .GetRequiredService<IApplicationPasswordService>()
                .ListAsync(arguments[1]).ConfigureAwait(false);
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

        if (Matches(arguments, 3, "--revoke-app-password"))
        {
            _ = Guid.TryParse(arguments[2], out var applicationPasswordId);
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var revoked = await scope.ServiceProvider
                .GetRequiredService<IApplicationPasswordService>()
                .RevokeAsync(arguments[1], applicationPasswordId).ConfigureAwait(false);
            Console.WriteLine(revoked
                ? "The application password is revoked."
                : "The application password was not found.");
            return revoked ? 0 : 1;
        }

        if (Matches(arguments, 3, "--enroll-totp"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .BeginTotpEnrollmentAsync(arguments[1], arguments[2]).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;
            Console.WriteLine($"Secret: {result.Secret}");
            Console.WriteLine($"URI: {result.ProvisioningUri}");
            return 0;
        }

        if (Matches(arguments, 3, "--confirm-totp"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .ConfirmTotpEnrollmentAsync(arguments[1], arguments[2]).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;
            foreach (var recoveryCode in result.RecoveryCodes ?? [])
                Console.WriteLine($"Recovery: {recoveryCode}");
            return 0;
        }

        if (Matches(arguments, 2, "--totp-status"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var status = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .GetStatusAsync(arguments[1]).ConfigureAwait(false);
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

        if (Matches(arguments, 2, "--regenerate-recovery-codes"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .RegenerateRecoveryCodesAsync(arguments[1]).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            if (!result.Succeeded)
                return 1;
            foreach (var recoveryCode in result.RecoveryCodes ?? [])
                Console.WriteLine($"Recovery: {recoveryCode}");
            return 0;
        }

        if (Matches(arguments, 2, "--disable-totp"))
        {
            using var host = BuildHost(arguments, environmentConfig);
            using var scope = host.Services.CreateScope();
            var disabled = await scope.ServiceProvider.GetRequiredService<IMfaService>()
                .DisableTotpAsync(arguments[1]).ConfigureAwait(false);
            Console.WriteLine(disabled
                ? "TOTP MFA is disabled and OAuth device grants are revoked."
                : "The account does not have a TOTP enrollment.");
            return disabled ? 0 : 1;
        }

        throw new InvalidOperationException("The management command is not supported.");
    }
    // A command process must return a nonzero status for every unhandled backend failure.
#pragma warning disable CA1031
    catch (Exception exception)
#pragma warning restore CA1031
    {
        await Console.Error.WriteLineAsync(
            $"The management command failed: {exception.GetBaseException().Message}")
            .ConfigureAwait(false);
        return 1;
    }
}
#pragma warning restore MA0051

static bool IsSupportedCommand(string[] arguments) =>
    arguments.SequenceEqual(["--healthcheck"])
    || arguments.SequenceEqual(["--initialize-empty-database"])
    || arguments.SequenceEqual(["--ensure-runtime-schema"])
    || Matches(arguments, 3, "--ensure-domain")
    || Matches(arguments, 4, "--create-account")
    || Matches(arguments, 3, "--set-catchall")
    || Matches(arguments, 3, "--set-domain-active")
    || Matches(arguments, 3, "--create-app-password")
    || Matches(arguments, 2, "--list-app-passwords")
    || Matches(arguments, 3, "--revoke-app-password")
    || Matches(arguments, 3, "--purge-quarantined-smoke-message")
    || Matches(arguments, 3, "--export-distributed-snapshot")
    || Matches(arguments, 3, "--restore-distributed-snapshot")
    || Matches(arguments, 2, "--verify-distributed-snapshot")
    || arguments.Length == 6 && arguments[0] is
        ("--publish-distributed-archive" or "--fetch-distributed-archive")
    || Matches(arguments, 3, "--enroll-totp")
    || Matches(arguments, 3, "--confirm-totp")
    || Matches(arguments, 2, "--totp-status")
    || Matches(arguments, 2, "--regenerate-recovery-codes")
    || Matches(arguments, 2, "--disable-totp")
    || arguments.Length == 2 && arguments[0] is
        ("--validate-gateway-config" or "--validate-worker-config"
            or "--healthcheck-gateway"
            or "--probe-gateway-backends" or "--probe-worker-backends"
            or "--probe-worker-dispatch" or "--audit-blob-references");

static bool Matches(string[] arguments, int length, string command) =>
    arguments.Length == length && string.Equals(arguments[0], command, StringComparison.Ordinal);

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
