using System.Diagnostics;
using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

// Test-only phase data: never record request paths, headers, payloads, SQL or exception messages.
internal sealed class GatewayFixtureDiagnostics
{
    internal const int MaximumEvents = 128;
    private readonly Lock _sync = new();
    private readonly Queue<PhaseEvent> _events = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly AsyncLocal<DispatchIdentity?> _dispatch = new();
    private long _dropped;
    private string? _lastReport;

    public string? LastReport { get { lock (_sync) return _lastReport; } }

    public void Record(Phase phase, Guid? request = null, Operation operation = Operation.Other,
        Activity activity = Activity.None, Guid? span = null)
    {
        lock (_sync)
        {
            if (_events.Count == MaximumEvents) { _events.Dequeue(); _dropped++; }
            _events.Enqueue(new(_clock.ElapsedMilliseconds, phase.ToString(), request, operation.ToString(), activity.ToString(), span));
        }
    }

    public void RecordIo(Phase phase, Activity activity, Guid span)
    {
        if (_dispatch.Value is { } identity)
            Record(phase, identity.Request, identity.Operation, activity, span);
    }

    public string Report(JsonElement database, string workerState)
    {
        lock (_sync)
            return JsonSerializer.Serialize(new
            {
                version = 1,
                elapsedMilliseconds = _clock.ElapsedMilliseconds,
                droppedEvents = _dropped,
                events = _events.ToArray(),
                workerState,
                database
            });
    }

    public void Publish(string report)
    {
        lock (_sync) _lastReport = report;
        Console.WriteLine("MK8_TEST_HTTP_CANCELLED_DIAGNOSTICS " + report);
    }

    public static Operation Classify(string operation) => operation switch
    {
        ApplicationOperations.JmapProfileGet => Operation.Profile,
        ApplicationOperations.MailOperationExecute => Operation.Mail,
        ApplicationOperations.ImapAuthenticatePassword => Operation.Authenticate,
        _ => Operation.Other,
    };

    internal enum Phase
    {
        ClientSend, ClientHeaders, ClientCancelled, GatewayEnter, GatewayExit,
        JournalInboundStart, JournalInboundComplete, JournalOutboundStart, JournalOutboundComplete,
        DispatchStart, DispatchComplete, DispatchFault, IoStart, IoReturned, IoFault, IoCancelled
    }
    internal enum Operation { Other, Authenticate, Profile, Mail }
    internal enum Activity { None, DbReader, DbScalar, DbNonQuery, OtherDb, BlobPut, BlobRead, BlobDelete }
    private sealed record DispatchIdentity(Guid Request, Operation Operation);
    private sealed record PhaseEvent(long Milliseconds, string Phase, Guid? Request, string Operation, string Activity, Guid? Span);

    internal sealed class Dispatcher(IApplicationRequestDispatcher inner, GatewayFixtureDiagnostics diagnostics)
        : IApplicationRequestDispatcher
    {
        public async Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
        {
            var operation = Classify(request.Operation);
            var previous = diagnostics._dispatch.Value;
            diagnostics._dispatch.Value = new(request.Id, operation);
            diagnostics.Record(Phase.DispatchStart, request.Id, operation);
            var completed = false;
            try
            {
                var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                completed = true;
                return response;
            }
            finally
            {
                diagnostics.Record(completed ? Phase.DispatchComplete : Phase.DispatchFault, request.Id, operation);
                diagnostics._dispatch.Value = previous;
            }
        }
    }
}
