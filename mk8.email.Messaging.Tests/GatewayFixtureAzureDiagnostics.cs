using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Activity = mk8.email.Messaging.Tests.GatewayFixtureDiagnostics.Activity;
using Phase = mk8.email.Messaging.Tests.GatewayFixtureDiagnostics.Phase;

namespace mk8.email.Messaging.Tests;

// BeforeTransport observes SDK attempts, not response-body consumption or network root cause.
internal sealed class GatewayFixtureAzureDiagnostics(GatewayFixtureDiagnostics diagnostics) : HttpPipelinePolicy
{
    public static void Configure(BlobClientOptions options, GatewayFixtureDiagnostics diagnostics) =>
        options.AddPolicy(new GatewayFixtureAzureDiagnostics(diagnostics), HttpPipelinePosition.BeforeTransport);

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var attempt = Start(message);
        using var scope = diagnostics.EnterAzureAttempt(attempt.Activity, attempt.Span, attempt.Number);
        var outcome = Phase.AzureFault;
        try { ProcessNext(message, pipeline); outcome = Phase.AzureReturned; }
        catch (OperationCanceledException) when (message.CancellationToken.IsCancellationRequested)
        { outcome = Phase.AzureCancelled; throw; }
        finally { Complete(message, attempt, outcome); }
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        var attempt = Start(message);
        using var scope = diagnostics.EnterAzureAttempt(attempt.Activity, attempt.Span, attempt.Number);
        var outcome = Phase.AzureFault;
        try { await ProcessNextAsync(message, pipeline).ConfigureAwait(false); outcome = Phase.AzureReturned; }
        catch (OperationCanceledException) when (message.CancellationToken.IsCancellationRequested)
        { outcome = Phase.AzureCancelled; throw; }
        finally { Complete(message, attempt, outcome); }
    }

    private Attempt Start(HttpMessage message)
    {
        if (!message.TryGetProperty(typeof(Identity), out var value))
        {
            value = new Identity();
            message.SetProperty(typeof(Identity), value);
        }
        var identity = (Identity)value!;
        var attempt = new Attempt(identity.Span, Interlocked.Increment(ref identity.Count), Classify(message.Request));
        diagnostics.RecordIo(Phase.AzureStart, attempt.Activity, attempt.Span, attempt.Number);
        return attempt;
    }

    private void Complete(HttpMessage message, Attempt attempt, Phase outcome) =>
        diagnostics.RecordIo(outcome, attempt.Activity, attempt.Span, attempt.Number,
            outcome is Phase.AzureReturned && message.HasResponse ? message.Response.Status : null);

    internal static Activity Classify(Request request)
    {
        // Inspect only fixed SDK operation selectors; never retain URI, SAS, names or headers.
        var container = false;
        var component = Activity.AzureUpload;
        var sawContainer = false;
        var sawComponent = false;
        var ambiguous = false;
        var query = request.Uri.Query.AsSpan().TrimStart('?');
        foreach (var range in query.Split('&'))
        {
            var pair = query[range];
            if (pair.StartsWith("restype=", StringComparison.Ordinal))
            {
                ambiguous |= sawContainer || !pair.SequenceEqual("restype=container");
                sawContainer = true;
                container = true;
            }
            else if (pair.StartsWith("comp=", StringComparison.Ordinal))
            {
                ambiguous |= sawComponent;
                sawComponent = true;
                component = pair switch
                {
                    "comp=block" => Activity.AzureBlock,
                    "comp=blocklist" => Activity.AzureBlockList,
                    _ => Activity.AzureOther,
                };
            }
        }
        if (ambiguous) return Activity.AzureOther;
        if (container) return request.Method == RequestMethod.Put && component is Activity.AzureUpload
            ? Activity.AzureContainerCreate : Activity.AzureOther;
        if (component is Activity.AzureOther || (sawComponent && request.Method != RequestMethod.Put)) return Activity.AzureOther;
        if (request.Method == RequestMethod.Put) return component;
        if (request.Method == RequestMethod.Head) return Activity.AzureProperties;
        if (request.Method == RequestMethod.Get) return Activity.AzureDownload;
        if (request.Method == RequestMethod.Delete) return Activity.AzureDelete;
        return Activity.AzureOther;
    }

    private sealed class Identity
    {
        public Guid Span { get; } = Guid.CreateVersion7();
        public int Count;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly record struct Attempt(Guid Span, int Number, Activity Activity);
}
