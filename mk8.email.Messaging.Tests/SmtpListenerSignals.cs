using Microsoft.Extensions.Logging;
using mk8.email.Smtp.Presentation;

namespace mk8.email.Messaging.Tests;

internal sealed class SmtpListenerSignals : ILogger<SmtpServerService>
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (eventId.Id == 3202) Started.TrySetResult();
    }
}
