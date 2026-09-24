namespace mk8.email.Application.Protocol;

internal readonly record struct ParsedMailMessage(
    string Subject,
    string Body,
    string Headers);
