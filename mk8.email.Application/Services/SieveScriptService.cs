using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

internal sealed class SieveScriptService(EmailDbContext database) : ISieveScriptService
{
    public SieveCompilationResult Validate(string content) => SieveScript.Compile(content);

    public async Task<SieveScriptOperationResult> CheckSpaceAsync(
        Guid userId,
        string name,
        long contentSizeBytes,
        int maximumScripts,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(name, out var normalized))
            return Failure("The script name is invalid.");
        if (contentSizeBytes is <= 0 or > SieveScript.MaximumScriptBytes)
            return Failure("The script size exceeds the configured limit.", "QUOTA/MAXSIZE");
        if (maximumScripts < 1)
            return Failure("The maximum number of scripts has been reached.", "QUOTA/MAXSCRIPTS");

        var exists = await database.SieveScripts.AsNoTracking().AnyAsync(
            script => script.UserId == userId && script.Name == normalized,
            cancellationToken);
        if (exists)
            return Success();
        var count = await database.SieveScripts.AsNoTracking().CountAsync(
            script => script.UserId == userId,
            cancellationToken);
        return count < maximumScripts
            ? Success()
            : Failure("The maximum number of scripts has been reached.", "QUOTA/MAXSCRIPTS");
    }

    public async Task<IReadOnlyList<SieveScriptSummary>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        await database.SieveScripts
            .AsNoTracking()
            .Where(script => script.UserId == userId)
            .OrderBy(script => script.Name)
            .Select(script => new SieveScriptSummary(
                script.Name,
                script.IsActive,
                script.CreatedAt,
                script.UpdatedAt))
            .ToListAsync(cancellationToken);

    public async Task<StoredSieveScript?> GetAsync(
        Guid userId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(name, out var normalized))
            return null;
        return await database.SieveScripts
            .AsNoTracking()
            .Where(script => script.UserId == userId && script.Name == normalized)
            .Select(script => new StoredSieveScript(
                script.Name,
                script.Content,
                script.IsActive,
                script.CreatedAt,
                script.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<SieveScriptOperationResult> PutAsync(
        Guid userId,
        string name,
        string content,
        int maximumScripts = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(name, out var normalized))
            return Failure("The script name is invalid.");
        if (Encoding.UTF8.GetByteCount(content) is 0 or > SieveScript.MaximumScriptBytes)
            return Failure("The script must contain from 1 through 1048576 bytes.", "QUOTA/MAXSIZE");
        var compilation = Validate(content);
        if (!compilation.Succeeded)
            return Failure(FormatDiagnostic(compilation.Diagnostics[0]));
        if (!await database.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken))
            return Failure("The user does not exist.");

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var now = DateTime.UtcNow;
        var script = await database.SieveScripts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Name == normalized,
            cancellationToken);
        if (script is null)
        {
            var scriptCount = await database.SieveScripts.CountAsync(
                item => item.UserId == userId,
                cancellationToken);
            if (scriptCount >= maximumScripts)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                return Failure("The maximum number of scripts has been reached.", "QUOTA/MAXSCRIPTS");
            }
            database.SieveScripts.Add(new SieveScriptDB
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                Name = normalized,
                Content = content,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            script.Content = content;
            script.UpdatedAt = now;
        }

        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return Success();
    }

    public async Task<SieveScriptOperationResult> SetActiveAsync(
        Guid userId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        string? normalized = null;
        if (!string.IsNullOrEmpty(name) && !TryNormalizeName(name, out normalized))
            return Failure("The script name is invalid.");

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;
        SieveScriptDB? target = null;
        if (normalized is not null)
        {
            target = await database.SieveScripts.SingleOrDefaultAsync(
                script => script.UserId == userId && script.Name == normalized,
                cancellationToken);
            if (target is null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);
                return Failure("The script does not exist.", "NONEXISTENT");
            }
        }

        var current = await database.SieveScripts
            .Where(script => script.UserId == userId && script.IsActive)
            .SingleOrDefaultAsync(cancellationToken);
        if (current is not null
            && (normalized is null || !string.Equals(current.Name, normalized, StringComparison.Ordinal)))
        {
            current.IsActive = false;
            current.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        if (target is not null)
        {
            target.IsActive = true;
            target.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return Success();
    }

    public async Task<SieveScriptOperationResult> DeleteAsync(
        Guid userId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(name, out var normalized))
            return Failure("The script name is invalid.");
        var script = await database.SieveScripts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Name == normalized,
            cancellationToken);
        if (script is null)
            return Failure("The script does not exist.", "NONEXISTENT");
        if (script.IsActive)
            return Failure("The active script cannot be deleted.", "ACTIVE");
        database.SieveScripts.Remove(script);
        await database.SaveChangesAsync(cancellationToken);
        return Success();
    }

    public async Task<SieveScriptOperationResult> RenameAsync(
        Guid userId,
        string oldName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(oldName, out var normalizedOld)
            || !TryNormalizeName(newName, out var normalizedNew))
            return Failure("A script name is invalid.");
        if (await database.SieveScripts.AnyAsync(
                script => script.UserId == userId && script.Name == normalizedNew,
                cancellationToken))
            return Failure("The destination script already exists.", "ALREADYEXISTS");
        var script = await database.SieveScripts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Name == normalizedOld,
            cancellationToken);
        if (script is null)
            return Failure("The script does not exist.", "NONEXISTENT");
        script.Name = normalizedNew;
        script.UpdatedAt = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Success();
    }

    private static bool TryNormalizeName(string name, out string normalized)
    {
        if (!MailboxName.IsWellFormedUnicode(name))
        {
            normalized = string.Empty;
            return false;
        }
        normalized = name.Normalize(NormalizationForm.FormC);
        return normalized.Length > 0
            && normalized.EnumerateRunes().Count() <= 128
            && Encoding.UTF8.GetByteCount(normalized) <= 512
            && !normalized.EnumerateRunes().Any(rune =>
                rune.Value is >= 0x0000 and <= 0x001f
                    or >= 0x007f and <= 0x009f
                    or 0x2028
                    or 0x2029);
    }

    private static string FormatDiagnostic(SieveDiagnostic diagnostic) =>
        $"Line {diagnostic.Line}, column {diagnostic.Column}: {diagnostic.Message}";

    private static SieveScriptOperationResult Success() => new(true);
    private static SieveScriptOperationResult Failure(string error, string? responseCode = null) =>
        new(false, error, responseCode);
}
