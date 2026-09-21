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
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeName(name, out var normalized))
            return Failure("The script name is invalid.");
        var compilation = Validate(content);
        if (!compilation.Succeeded)
            return Failure(FormatDiagnostic(compilation.Diagnostics[0]));
        if (!await database.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken))
            return Failure("The user does not exist.");

        var now = DateTime.UtcNow;
        var script = await database.SieveScripts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Name == normalized,
            cancellationToken);
        if (script is null)
        {
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

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        SieveScriptDB? target = null;
        if (normalized is not null)
        {
            target = await database.SieveScripts.SingleOrDefaultAsync(
                script => script.UserId == userId && script.Name == normalized,
                cancellationToken);
            if (target is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure("The script does not exist.");
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
            return Failure("The script does not exist.");
        if (script.IsActive)
            return Failure("The active script cannot be deleted.");
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
            return Failure("The destination script already exists.");
        var script = await database.SieveScripts.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Name == normalizedOld,
            cancellationToken);
        if (script is null)
            return Failure("The script does not exist.");
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
            && Encoding.UTF8.GetByteCount(normalized) <= 128
            && !normalized.Any(char.IsControl);
    }

    private static string FormatDiagnostic(SieveDiagnostic diagnostic) =>
        $"Line {diagnostic.Line}, column {diagnostic.Column}: {diagnostic.Message}";

    private static SieveScriptOperationResult Success() => new(true);
    private static SieveScriptOperationResult Failure(string error) => new(false, error);
}
