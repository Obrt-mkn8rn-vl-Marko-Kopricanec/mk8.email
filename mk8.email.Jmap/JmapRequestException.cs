namespace mk8.email.Jmap;

public sealed class JmapRequestException(
    string type,
    int statusCode,
    string title,
    string? detail = null,
    string? limit = null) : Exception(detail ?? title)
{
    public string Type { get; } = type;
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string? Limit { get; } = limit;
}
