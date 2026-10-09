using System.Diagnostics.Tracing;
using Phase = mk8.email.Messaging.Tests.GatewayFixtureDiagnostics.Phase;

namespace mk8.email.Messaging.Tests;

// Test-only .NET HTTP lifecycle points, not provider progress or response-body/commit proof.
internal sealed class GatewayFixtureHttpDiagnostics : EventListener
{
    internal const string ControlledSourceName = "mk8.email.tests.http-events";
    private readonly GatewayFixtureDiagnostics? _diagnostics;
    private readonly string? _sourceName;

    public GatewayFixtureHttpDiagnostics(GatewayFixtureDiagnostics diagnostics, bool controlledSource = false)
    {
        _diagnostics = diagnostics;
        _sourceName = controlledSource ? ControlledSourceName : "System.Net.Http";
        // EventListener calls virtual callbacks from its base constructor, before these fields exist.
        // Re-enumeration handles pre-existing sources; later creations use the same guarded callback.
        foreach (var source in EventSource.GetSources()) EnableSelectedSource(source);
    }

    protected override void OnEventSourceCreated(EventSource eventSource) => EnableSelectedSource(eventSource);

    private void EnableSelectedSource(EventSource source)
    {
        if (_diagnostics is not null && string.Equals(source.Name, _sourceName, StringComparison.Ordinal))
            EnableEvents(source, EventLevel.Informational, EventKeywords.None);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_diagnostics is not { } diagnostics || !string.Equals(eventData.EventSource.Name, _sourceName, StringComparison.Ordinal)) return;
        var phase = eventData.EventName switch
        {
            "RequestStart" => Phase.HttpRequestStart,
            "RequestLeftQueue" => Phase.HttpRequestLeftQueue,
            "RequestHeadersStart" => Phase.HttpRequestHeadersStart,
            "RequestHeadersStop" => Phase.HttpRequestHeadersStop,
            "ResponseHeadersStart" => Phase.HttpResponseHeadersStart,
            "ResponseHeadersStop" => Phase.HttpResponseHeadersStop,
            "RequestStop" => Phase.HttpRequestStop,
            "RequestFailed" => Phase.HttpRequestFailed,
            _ => (Phase?)null,
        };
        // Do NOT access Payload/PayloadNames: they may contain paths, hosts, headers or failures.
        if (phase is { } selected) diagnostics.RecordHttp(selected, eventData.ActivityId);
    }
}
