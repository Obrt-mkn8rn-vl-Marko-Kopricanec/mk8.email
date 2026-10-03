using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Data;
using mk8.email.Configuration;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

internal sealed class JmapVacationResponseService(
    EmailDbContext database,
    VacationResponseContentService content)
{
    public Task SetBodiesAsync(
        JmapVacationResponseDB response,
        string? textBody,
        string? htmlBody,
        CancellationToken cancellationToken) =>
        content.SetAsync(response, textBody, htmlBody, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    public async Task<JmapVacationResponseDB> GetOrCreateAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var response = await database.JmapVacationResponses.FirstOrDefaultAsync(
            item => item.AccountId == accountId,
            cancellationToken).ConfigureAwait(false);
        if (response is not null)
            return response;

        response = new JmapVacationResponseDB
        {
            AccountId = accountId,
            IsEnabled = false,
            UpdatedAt = DateTime.UtcNow,
        };
        var preexistingChanges = database.ChangeTracker.Entries<JmapChangeDB>()
            .Select(entry => entry.Entity)
            .ToHashSet();
        await database.JmapVacationResponses.AddAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (DbUpdateException)
        {
            database.Entry(response).State = EntityState.Detached;
            foreach (var entry in database.ChangeTracker.Entries<JmapChangeDB>()
                         .Where(entry => entry.State == EntityState.Added
                             && !preexistingChanges.Contains(entry.Entity)))
            {
                entry.State = EntityState.Detached;
            }
            var stored = await database.JmapVacationResponses.FirstOrDefaultAsync(
                item => item.AccountId == accountId,
                cancellationToken).ConfigureAwait(false);
            if (stored is null)
                throw;
            return stored;
        }
    }

}
