using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Infrastructure.Data;

namespace mk8.email.Application.Services;

public sealed class DatabaseInitializationService(
    EmailDbContext db,
    ISeederService seeder) : IDatabaseInitializationService
{
    public async Task<AdministrationResult> InitializeEmptyDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
            """;
            var tableCount = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (tableCount != 0)
            {
                return new AdministrationResult(
                    false,
                    "The database contains tables. Initialization stopped without changes.");
            }

            if (!await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false))
                return new AdministrationResult(false, "The database schema was not created.");

            await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            return new AdministrationResult(true, "The empty database was initialized.");
        }
    }
}
