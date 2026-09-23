using Npgsql;

namespace mk8.email.Messaging;

public sealed record Pop3MaildropLease(Guid UserId, Guid OwnerToken);

public interface IPop3MaildropLeaseStore
{
    Task<Pop3MaildropLease?> TryAcquireAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task<bool> RenewAsync(
        Pop3MaildropLease lease,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        Pop3MaildropLease lease,
        CancellationToken cancellationToken = default);
}

public sealed class PostgresPop3MaildropLeaseStore(NpgsqlDataSource dataSource)
    : IPop3MaildropLeaseStore
{
    public async Task<Pop3MaildropLease?> TryAcquireAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ValidateUserAndLifetime(userId, lifetime);
        var token = Guid.CreateVersion7();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pop3_maildrop_leases (user_id, owner_token, expires_at)
            VALUES (@user_id, @owner_token, clock_timestamp() + @lifetime)
            ON CONFLICT (user_id) DO UPDATE
                SET owner_token = EXCLUDED.owner_token,
                    expires_at = EXCLUDED.expires_at
                WHERE pop3_maildrop_leases.expires_at <= clock_timestamp()
            RETURNING owner_token
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("owner_token", token);
        command.Parameters.AddWithValue("lifetime", lifetime);
        var acquired = await command.ExecuteScalarAsync(cancellationToken);
        return acquired is Guid ownerToken
            ? new Pop3MaildropLease(userId, ownerToken)
            : null;
    }

    public async Task<bool> RenewAsync(
        Pop3MaildropLease lease,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateUserAndLifetime(lease.UserId, lifetime);
        if (lease.OwnerToken == Guid.Empty)
            throw new ArgumentException("The POP3 maildrop owner token is invalid.", nameof(lease));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE pop3_maildrop_leases
               SET expires_at = clock_timestamp() + @lifetime
             WHERE user_id = @user_id
               AND owner_token = @owner_token
               AND expires_at > clock_timestamp()
            """;
        command.Parameters.AddWithValue("user_id", lease.UserId);
        command.Parameters.AddWithValue("owner_token", lease.OwnerToken);
        command.Parameters.AddWithValue("lifetime", lifetime);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseAsync(
        Pop3MaildropLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.UserId == Guid.Empty || lease.OwnerToken == Guid.Empty)
            throw new ArgumentException("The POP3 maildrop lease is invalid.", nameof(lease));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM pop3_maildrop_leases
             WHERE user_id = @user_id
               AND owner_token = @owner_token
            """;
        command.Parameters.AddWithValue("user_id", lease.UserId);
        command.Parameters.AddWithValue("owner_token", lease.OwnerToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateUserAndLifetime(Guid userId, TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("The POP3 maildrop user identifier is invalid.", nameof(userId));
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "The POP3 maildrop lease must be positive and no longer than 24 hours.");
        }
    }
}
