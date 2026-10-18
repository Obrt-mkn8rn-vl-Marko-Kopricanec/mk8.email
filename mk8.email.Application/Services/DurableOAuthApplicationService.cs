using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Data;
using Npgsql;

namespace mk8.email.Application.Services;

// A durable bus retry is not a second use of the OAuth credential. Persist its
// encrypted result in the SAME SQL transaction as rotation/code consumption.
internal sealed class DurableOAuthApplicationService(EmailDbContext database,
    OAuthTokenService tokens, OAuthAuthorizationService authorization,
    ApplicationOperationReceiptStore receipts, LargeObjectTransactionEffects effects)
{
    private readonly LargeObjectTransactionEffects _effects = effects;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    internal Task<OAuthTokenApplicationResult> RefreshTokenAsync(Guid operationId,
        OAuthRefreshTokenRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(operationId, "oauth.refresh", request, request.RefreshToken, "mk8_rt_",
            code: false, cancellation => tokens.RefreshWithinReceiptAsync(request.RefreshToken, request.ClientId, cancellation), token);
    }

    internal Task<OAuthTokenApplicationResult> RedeemAuthorizationCodeAsync(Guid operationId,
        OAuthAuthorizationCodeRedeemRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(operationId, "oauth.redeem", request, request.Code, "mk8_ac_",
            code: true, cancellation => authorization.RedeemWithinReceiptAsync(request.Code, request.ClientId,
                request.RedirectUri, request.CodeVerifier, cancellation), token);
    }

    private async Task<OAuthTokenApplicationResult> ExecuteAsync<T>(Guid operationId, string purpose, T input,
        string credential, string prefix, bool code, Func<CancellationToken, Task<OAuthTokenPair?>> invoke,
        CancellationToken token)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A durable OAuth operation identity is required.", nameof(operationId));
        if (!string.Equals(database.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal)
            || database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Durable OAuth requires its own PostgreSQL transaction.");
        }
        if (!TryCredentialId(credential, prefix, out var credentialId)) return new(Token: null);

        // Serialize SAME-operation callers BEFORE a Serializable snapshot exists.
        // The two-int advisory namespace is distinct from the receipt store's
        // bigint transaction locks. A nonpooled connection owns this session lock
        // through all SQL/Blob settlement; closing it releases even a failed body.
        var connectionString = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString())
        {
            Pooling = false,
            Enlist = false,
        };
        var outcome = new DurableOAuthOutcome(operationId);
        var lockConnection = new NpgsqlConnection(connectionString.ConnectionString);
        return await outcome.OwnAsync(lockConnection, async () =>
        {
            await AcquireOperationLockAsync(lockConnection, operationId, token).ConfigureAwait(false);
            return await ExecuteTransactionAsync(operationId, purpose, input, credentialId, code, invoke, outcome, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<OAuthTokenApplicationResult> ExecuteTransactionAsync<T>(Guid operationId, string purpose,
        T input, Guid credentialId, bool code, Func<CancellationToken, Task<OAuthTokenPair?>> invoke,
        DurableOAuthOutcome outcome, CancellationToken token)
    {
        var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
        return await outcome.OwnAsync(transaction, () => ExecuteReceiptAsync(
            operationId, purpose, input, credentialId, code, invoke, transaction, outcome, token)).ConfigureAwait(false);
    }

    private async Task<OAuthTokenApplicationResult> ExecuteReceiptAsync<T>(Guid operationId, string purpose,
        T input, Guid credentialId, bool code, Func<CancellationToken, Task<OAuthTokenPair?>> invoke,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, DurableOAuthOutcome outcome, CancellationToken token)
    {
        // An operation receipt outlives the consumed credential's ordinary
        // retention. Recover its bound identity before requiring that row.
        var userId = await database.ApplicationOperationReceipts
            .Where(receipt => receipt.OperationId == operationId && receipt.StepNumber == 0)
            .Select(receipt => receipt.UserId).SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (userId == Guid.Empty)
        {
            userId = code
                ? await database.OAuthAuthorizationCodes.Where(candidate => candidate.Id == credentialId)
                    .Select(candidate => candidate.UserId).SingleOrDefaultAsync(token).ConfigureAwait(false)
                : await database.OAuthTokens.Where(candidate => candidate.Id == credentialId)
                    .Select(candidate => candidate.Grant.UserId).SingleOrDefaultAsync(token).ConfigureAwait(false);
        }
        if (userId == Guid.Empty) return new(Token: null);
        var inputHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, JsonOptions)));
        var key = new ApplicationReceiptKey(operationId, 0, userId, purpose, inputHash);
        var replay = await receipts.FindLockedAsync(key, token).ConfigureAwait(false);
        if (replay is not null)
        {
            var result = JsonSerializer.Deserialize<OAuthTokenApplicationResult>(replay.Result.Span, JsonOptions)
                ?? throw new InvalidOperationException("The OAuth result receipt is incomplete.");
            outcome.RequireReceiptRecovery();
            return result;
        }

        var marker = _effects.Mark();
        var commitAttempted = false;
        try
        {
            var pair = await invoke(token).ConfigureAwait(false);
            var result = new OAuthTokenApplicationResult(pair is null ? null : new OAuthTokenValue(
                pair.GrantId, pair.AccessToken, pair.RefreshToken, pair.ExpiresInSeconds, pair.Scope, pair.IdToken));
            await receipts.SaveAsync(key, new ApplicationReceiptContent(
                JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions), []), token).ConfigureAwait(false);
            commitAttempted = true;
            // This state is set BEFORE the actual commit. A failed commit ACK
            // cannot distinguish rollback from a committed encrypted receipt.
            outcome.RequireReceiptRecovery();
            await transaction.CommitAsync(token).ConfigureAwait(false);
            await _effects.CommitAsync(marker).ConfigureAwait(false);
            return result;
        }
        catch (Exception original)
        {
            // A failed commit acknowledgement is ambiguous: never delete a Blob
            // that may already be referenced by a committed credential receipt.
            await RecoverAsync(transaction, marker, commitAttempted, original).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RecoverAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        int marker, bool commitAttempted, Exception original)
    {
        try
        {
            if (commitAttempted)
            {
                _effects.Discard(marker);
                return;
            }
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _effects.Discard(marker); // Ambiguous rollback: retain potentially referenced objects.
                throw;
            }
            await _effects.RollbackAsync(marker).ConfigureAwait(false);
        }
        catch (Exception cleanup)
        {
            throw new AggregateException(original, cleanup);
        }
    }

    private static bool TryCredentialId(string? value, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return value?.StartsWith(prefix, StringComparison.Ordinal) is true
            && value.Length > prefix.Length + 33 && value[prefix.Length + 32] == '_'
            && Guid.TryParseExact(value.AsSpan(prefix.Length, 32), "N", out id);
    }

    private static async Task AcquireOperationLockAsync(NpgsqlConnection connection, Guid operationId,
        CancellationToken token)
    {
        await connection.OpenAsync(token).ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText = "SELECT pg_advisory_lock(1296775247, @operation)";
        command.Parameters.AddWithValue("operation", BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(operationId.ToByteArray())));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
