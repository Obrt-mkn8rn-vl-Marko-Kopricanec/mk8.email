using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace mk8.email.Messaging.Tests;

internal sealed class NativeHostScanner : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly TaskCompletionSource<string> _scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stopping;
    private Task? _disposing;

    public NativeHostScanner(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}"));
        _application = builder.Build();
        _application.MapPost("/checkv2", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            _scanned.TrySetResult(await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false));
            return Results.Json(new { action = "no action", score = 0, required_score = 10, symbols = new { } });
        });
    }

    public Task<string> Scanned => _scanned.Task;
    public bool Retired { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) => _application.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _stopping ??= _application.StopAsync(CancellationToken.None);
        await _stopping.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
        _disposing ??= _application.DisposeAsync().AsTask();
        await _disposing.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
        Retired = true;
    }
}
