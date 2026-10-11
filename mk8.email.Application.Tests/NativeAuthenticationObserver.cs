using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using mk8.email.Application.Services;

namespace mk8.email.Application.Tests;

internal sealed class NativeAuthenticationObserver(Action<string>? write = null) : IMailAuthenticationStageObserver
{
    internal const int Capacity = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly Lock _gate = new();
    private readonly Queue<AuthenticationEvent> _events = new();
    private readonly Queue<AuthenticationStageEvent> _stageEvents = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Action<string> _write = write ?? Console.WriteLine;
    private long _dropped;
    private long _stageDropped;

    public void Record(Guid call, MailAuthenticationStage stage, MailAuthenticationStagePhase phase)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(call, Guid.Empty);
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        lock (_gate)
        {
            if (_stageEvents.Count == Capacity)
            {
                _stageEvents.Dequeue();
                _stageDropped++;
            }
            _stageEvents.Enqueue(new AuthenticationStageEvent(
                call, stage, phase, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
        }
    }

    public Guid Enter(bool primary)
    {
        var span = Guid.NewGuid();
        Record(span, primary, AuthenticationPhase.Entered);
        return span;
    }

    public void Record(Guid span, bool primary, AuthenticationPhase phase)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(span, Guid.Empty);
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        lock (_gate)
        {
            if (_events.Count == Capacity)
            {
                _events.Dequeue();
                _dropped++;
            }
            _events.Enqueue(new AuthenticationEvent(
                span, primary, phase, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
        }
    }

    public AuthenticationSnapshot Snapshot(AuthenticationPoint point)
    {
        if (!Enum.IsDefined(point)) throw new ArgumentOutOfRangeException(nameof(point));
        ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
        lock (_gate)
        {
            return new AuthenticationSnapshot(
                point, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                _dropped, Array.AsReadOnly(_events.ToArray()), ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount, availableWorkers,
                Array.AsReadOnly(_stageEvents.ToArray()), _stageDropped);
        }
    }

    public bool WritePoint(AuthenticationPoint point)
    {
        var value = JsonSerializer.Serialize(Snapshot(point), JsonOptions);
        try
        {
            _write("MK8_NATIVE_AUTH_DIAGNOSTIC " + value);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
