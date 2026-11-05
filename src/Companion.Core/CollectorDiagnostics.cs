using System.Security.Principal;
using System.Text.Json;

namespace Companion.Core;

public static class CollectorDiagnostics
{
    public static async Task<int> RunAsync(int gamePid, string outputDirectory, string mode, bool restartService = false)
    {
        string directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory)) throw new IOException("Collector diagnostic output must be a new directory.");
        if (mode is not ("Standard" or "Deep")) throw new ArgumentException("Choose Standard or Deep.");
        Directory.CreateDirectory(directory);
        string session = Guid.NewGuid().ToString("N");
        var request = new CollectorRequest(session, directory, gamePid, Environment.ProcessId, 5, mode);
        var diagnostics = new List<string>();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        using var collector = new CollectorSession();
        Exception? failure = null;
        try
        {
            collector.Validate();
            await collector.PrepareAsync(request).ConfigureAwait(false);
            await PerfViewCollector.WriteAsync(Path.Combine(directory, "recorder.json"), new { StartUtc = DateTimeOffset.UtcNow }).ConfigureAwait(false);
            await collector.BeginAsync(request).ConfigureAwait(false);
            await Task.Delay(5500).ConfigureAwait(false);
            if (restartService)
            {
                string marker = Path.Combine(Path.GetDirectoryName(directory)!, "restart.request");
                await File.WriteAllTextAsync(marker, session).ConfigureAwait(false);
                string restarted = Path.Combine(AppContext.BaseDirectory, "validation-restarted.json");
                using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(170));
                while (!File.Exists(restarted)) await Task.Delay(200, restartTimeout.Token).ConfigureAwait(false);
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(restarted, restartTimeout.Token).ConfigureAwait(false));
                if (receipt.RootElement.GetProperty("SessionId").GetString() != session) throw new IOException("Service restart test receipt belongs to another session.");
            }
        }
        catch (Exception error) { failure = error; }
        try { await collector.FinishAsync(request, diagnostics).ConfigureAwait(false); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        TraceSummary? summary = null;
        int waits = 0, disk = 0;
        try
        {
            if (failure is null)
            {
                summary = JsonSerializer.Deserialize<TraceSummary>(await File.ReadAllTextAsync(Path.Combine(directory, "cpu-analysis.json")).ConfigureAwait(false));
                using var coverage = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "trace-coverage.json")).ConfigureAwait(false));
                waits = coverage.RootElement.GetProperty("CompleteWaitIntervals").GetInt32();
                disk = coverage.RootElement.GetProperty("DiskEvents").GetInt32();
                if (summary?.ProcessId != gamePid || summary.Samples <= 0) throw new IOException("No CPU samples were attributed to the workload PID.");
                if (mode == "Deep" && (waits <= 0 || disk <= 0)) throw new IOException("The Deep workload produced no disk or context-switch evidence.");
                if (elevated) throw new IOException("The verification client was elevated; this does not prove unelevated capture.");
            }
        }
        catch (Exception error) { failure = error; }
        await PerfViewCollector.WriteAsync(Path.Combine(directory, "installed-collector-check.json"), new
        {
            Success = failure is null, Mode = mode, GamePid = gamePid, DesktopElevated = elevated,
            CpuSamples = summary?.Samples, ContextSwitchIntervals = waits, DiskEvents = disk,
            Diagnostics = diagnostics, Error = failure?.ToString()
        }).ConfigureAwait(false);
        return failure is null ? 0 : 1;
    }

    public static async Task<int> AbandonForVerificationAsync(int gamePid, string outputDirectory)
    {
        string directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory)) throw new IOException("Orphan verification output must be new.");
        Directory.CreateDirectory(directory);
        var request = new CollectorRequest(Guid.NewGuid().ToString("N"), directory, gamePid, Environment.ProcessId, 5, "Standard");
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await PerfViewCollector.WriteAsync(Path.Combine(directory, "collector-request.json"), request).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { await CollectorServiceClient.SendAsync(request, "start").ConfigureAwait(false); break; }
            catch (CollectorServiceException error) when (error.State is "Busy" or "Finalizing") { await Task.Delay(200, timeout.Token).ConfigureAwait(false); }
        }
        await PerfViewCollector.WriteAsync(Path.Combine(directory, "orphan-check.json"), new { Success = true, request.SessionId, GamePid = gamePid, StartUtc = start }).ConfigureAwait(false);
        return 0; // Deliberately exit without Stop/release to exercise the helper's desktop-exit watchdog.
    }
}
