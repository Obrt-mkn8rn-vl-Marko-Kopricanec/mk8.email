using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace mk8.email.Messaging.Tests;

internal sealed class NativeHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private readonly TestContext _context;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _exiting;
    private readonly TaskCompletionSource<Task> _stopBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const int StopRequested = 1;
    private const int StopAdmissionClosed = 2;
    private int _stopState;
    public Task GracefulCompleting { get; }

    private NativeHostProcess(Process process, TestContext context)
    {
        _process = process;
        _context = context;
        GracefulCompleting = _stopBody.Task.Unwrap();
        _output = ReadOutputAsync(process.StandardOutput);
        _error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        _exiting = process.WaitForExitAsync(CancellationToken.None);
    }

    public bool HasExited => _process.HasExited;
    public bool Retired { get; private set; }

    private async Task<string> ReadOutputAsync(StreamReader reader)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (output.Length + line.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("The owned host output exceeded the fixture bound.");
            output.AppendLine(line);
            if (IsStartedRecord(line)) _started.TrySetResult();
        }
        return output.ToString();
    }

    private static bool IsStartedRecord(string line)
    {
        if (!line.StartsWith('{')) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Category", out var category) && category.ValueKind == JsonValueKind.String
                && string.Equals(category.GetString(), "Microsoft.Hosting.Lifetime", StringComparison.Ordinal)
                && root.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.String
                && message.GetString()!.StartsWith("Application started.", StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    public async Task RequireStartedAsync(CancellationToken cancellationToken)
    {
        var observed = await Task.WhenAny(_started.Task, _output).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(observed, _started.Task))
            throw new InvalidOperationException("The owned host output ended before its startup record.");
        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static NativeHostProcess Start(string assemblyVariable, string configPath, TestContext context,
        string? argument = null, string? httpUrl = null)
    {
        var assembly = Environment.GetEnvironmentVariable(assemblyVariable);
        if (string.IsNullOrWhiteSpace(assembly) || !Path.IsPathFullyQualified(assembly)
            || !File.Exists(assembly) || !File.Exists(Path.ChangeExtension(assembly, ".deps.json"))
            || !File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
        {
            throw new AssertInconclusiveException("Explicit compiled host DLL, dependencies and runtime configuration are required.");
        }
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
        };
        start.ArgumentList.Add(assembly);
        if (argument is not null) start.ArgumentList.Add(argument);
        start.Environment["MK8EMAIL_CONFIG_FILE"] = configPath;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        if (httpUrl is not null) start.Environment["ASPNETCORE_URLS"] = httpUrl;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned host did not start.");
        return new NativeHostProcess(process, context);
    }

    public async Task RequireSuccessfulExitAsync(CancellationToken cancellationToken)
    {
        await _exiting.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Bound this caller's wait without cancelling the independently owned read.
        Assert.AreEqual(0, _process.ExitCode, await _error.WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task RequestGracefulStopAsync(CancellationToken cancellationToken)
    {
        var previous = Interlocked.CompareExchange(ref _stopState, StopRequested, 0);
        if ((previous & StopAdmissionClosed) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(NativeHostProcess)));
        if (previous == 0)
            _stopBody.SetResult(StopGracefullyAsync());
        // A pre-cancelled caller always receives cancellation, even if the
        // separately owned body finishes before this caller returns its task.
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : GracefulCompleting.WaitAsync(cancellationToken);
    }

    private async Task StopGracefullyAsync()
    {
        // Publish the owned task before entering native dispatch. Caller cancellation
        // bounds only its wait; disposal also retains this actual body below.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        NativeHostSignal.TerminateOwnedChild(_process);
        await Task.WhenAll(_exiting, _output, _error).WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, _process.ExitCode, "The owned host did not exit successfully after SIGTERM.");
        _context.WriteLine("Owned native host graceful SIGTERM exit: {0}", _process.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        // Seal stop admission before taking the join inventory. A body admitted
        // just before this transition is retained through its existing proxy,
        // even when the first caller has not linked its returned task yet.
        var stopAdmitted = (Interlocked.Or(ref _stopState, StopAdmissionClosed) & StopRequested) != 0;
        // Abrupt retirement is confined to this fixture's actual child handle;
        // it is not a graceful-stop, recovery or deployment claim.
        var errors = new List<Exception>();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        // A nonfatal kill failure cannot omit the independent exit/read join.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { errors.Add(error); }
        var tasks = !stopAdmitted
            ? new Task[] { _exiting, _output, _error }
            : [_exiting, _output, _error, GracefulCompleting];
        var joining = Task.WhenAll(tasks);
        try { await joining.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
        // WhenAll settles every actual task; collect its full ordinary fault inventory below.
#pragma warning disable CA1031
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
#pragma warning restore CA1031
        { if (!joining.IsCompleted) errors.Add(error); }
        foreach (var task in tasks)
        {
            if (task.IsFaulted) errors.AddRange(task.Exception!.Flatten().InnerExceptions);
            if (task.IsCanceled) errors.Add(new TaskCanceledException(task));
        }
        Retired = joining.IsCompleted && _process.HasExited;
        if (Retired)
        {
            var output = _output.IsCompletedSuccessfully ? await _output.WaitAsync(CancellationToken.None).ConfigureAwait(false) : "Owned stdout read failed.";
            var error = _error.IsCompletedSuccessfully ? await _error.WaitAsync(CancellationToken.None).ConfigureAwait(false) : "Owned stderr read failed.";
            _context.WriteLine("Owned native host retired: exit={0}\n{1}\n{2}", _process.ExitCode, output, error);
            _process.Dispose();
        }
        if (errors.Count > 0) throw new AggregateException("Owned host retirement failed.", errors);
    }
}
