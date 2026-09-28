using System.Globalization;
using System.Text.Json.Nodes;
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
        var response = await database.JmapVacationResponses.SingleOrDefaultAsync(
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
            var stored = await database.JmapVacationResponses.SingleOrDefaultAsync(
                item => item.AccountId == accountId,
                cancellationToken).ConfigureAwait(false);
            if (stored is null)
                throw;
            return stored;
        }
    }

    public async Task<JsonObject> ToJsonAsync(
        JmapVacationResponseDB response,
        IReadOnlySet<string>? properties,
        CancellationToken cancellationToken)
    {
        var result = new JsonObject { ["id"] = "singleton" };
        if (Wants("isEnabled")) result["isEnabled"] = response.IsEnabled;
        if (Wants("fromDate")) result["fromDate"] = FormatDate(response.FromDate);
        if (Wants("toDate")) result["toDate"] = FormatDate(response.ToDate);
        if (Wants("subject")) result["subject"] = response.Subject;
        if (Wants("textBody") || Wants("htmlBody"))
        {
            var bodies = await content.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (Wants("textBody")) result["textBody"] = bodies.TextBody;
            if (Wants("htmlBody")) result["htmlBody"] = bodies.HtmlBody;
        }
        return result;

        bool Wants(string property) => properties is null || properties.Contains(property);
    }

    public static bool TryParse(
        JsonObject value,
        int maximumBodyBytes,
        out VacationValues parsed,
        out IReadOnlyList<string> invalidProperties)
    {
        parsed = default;
        var invalid = new List<string>();
        if (!TryBoolean(value, "isEnabled", out var isEnabled)) invalid.Add("isEnabled");
        if (!TryUtcDate(value, "fromDate", out var fromDate)) invalid.Add("fromDate");
        if (!TryUtcDate(value, "toDate", out var toDate)) invalid.Add("toDate");
        if (!TryNullableString(value, "subject", out var subject) || subject is { Length: > 998 })
            invalid.Add("subject");
        if (!TryNullableString(value, "textBody", out var textBody)
            || textBody is not null && System.Text.Encoding.UTF8.GetByteCount(textBody) > maximumBodyBytes)
            invalid.Add("textBody");
        if (!TryNullableString(value, "htmlBody", out var htmlBody)
            || htmlBody is not null && System.Text.Encoding.UTF8.GetByteCount(htmlBody) > maximumBodyBytes)
            invalid.Add("htmlBody");
        var combinedBodyBytes = System.Text.Encoding.UTF8.GetByteCount(textBody ?? string.Empty)
            + System.Text.Encoding.UTF8.GetByteCount(htmlBody ?? string.Empty);
        if (combinedBodyBytes > Math.Max(0, maximumBodyBytes - 16_384))
        {
            invalid.Add("textBody");
            invalid.Add("htmlBody");
        }
        invalidProperties = invalid.Distinct(StringComparer.Ordinal).ToArray();
        if (invalidProperties.Count > 0)
            return false;
        parsed = new VacationValues(isEnabled, fromDate, toDate, subject, textBody, htmlBody);
        return true;
    }

    private static string? FormatDate(DateTime? value) =>
        value is null ? null : JmapDate.FormatUtc(value.Value);

    private static bool TryBoolean(JsonObject value, string name, out bool result)
    {
        result = false;
        return value[name] is JsonValue node && node.TryGetValue(out result);
    }

    private static bool TryUtcDate(JsonObject value, string name, out DateTime? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || text is null
            || !JmapDate.TryParseUtcDate(text, out var parsed))
            return false;
        result = parsed.UtcDateTime;
        return true;
    }

    private static bool TryNullableString(JsonObject value, string name, out string? result)
    {
        result = null;
        if (!value.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out result);
    }

    internal readonly record struct VacationValues(
        bool IsEnabled,
        DateTime? FromDate,
        DateTime? ToDate,
        string? Subject,
        string? TextBody,
        string? HtmlBody);
}
