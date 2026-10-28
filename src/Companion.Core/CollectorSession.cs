using System.Diagnostics;

namespace Companion.Core;

internal interface ICollectorSession : IDisposable
{
    void Validate();
    Task PrepareAsync(CollectorRequest request);
    Task FinishAsync(CollectorRequest request, List<string> diagnostics);
}

internal sealed class CollectorSession : ICollectorSession
{
    private Process? process;
    public void Validate() => PerfViewCollector.ValidateTool();
    public async Task PrepareAsync(CollectorRequest request)
    {
        string file = Path.Combine(request.Directory, "collector-request.json");
        await PerfViewCollector.WriteAsync(file, request).ConfigureAwait(false);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--collector"); start.ArgumentList.Add(file);
        process = Process.Start(start) ?? throw new IOException("The collector could not be started.");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(request.Directory, "collector-ready.json")))
        {
            if (process.HasExited || wait.Elapsed.TotalSeconds > 65)
                throw new IOException("PerfView did not become ready. No measurement was started; inspect collector-result.json.");
            await Task.Delay(200).ConfigureAwait(false);
        }
        var ready = System.Text.Json.JsonSerializer.Deserialize<CollectorResult>(await File.ReadAllTextAsync(Path.Combine(request.Directory, "collector-ready.json")).ConfigureAwait(false));
        if (ready?.Success != true || process.HasExited) throw new IOException("PerfView readiness could not be confirmed.");
    }
    public async Task FinishAsync(CollectorRequest request, List<string> diagnostics)
    {
        if (process is null) return;
        if (!process.HasExited)
            await File.WriteAllTextAsync(Path.Combine(request.Directory, "collector-stop.signal"), request.SessionId).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(130));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { diagnostics.Add("Collector is still finishing. Its watchdog will stop only its own ETW session."); }
        if (!process.HasExited) return;
        string receipt = Path.Combine(request.Directory, "collector-result.json");
        var result = File.Exists(receipt) ? System.Text.Json.JsonSerializer.Deserialize<CollectorResult>(await File.ReadAllTextAsync(receipt).ConfigureAwait(false)) : null;
        if (result?.Success != true) diagnostics.Add(result?.Message ?? "Collector did not leave a completion receipt.");
        else if (File.Exists(Path.Combine(request.Directory, "recorder.json")))
        {
            try { string? warning = await PerfViewCollector.ExportStacksAsync(request.Directory, request.GamePid).ConfigureAwait(false); if (warning is not null) diagnostics.Add(warning); }
            catch (Exception ex) { diagnostics.Add("Raw trace retained; automatic CPU stack export failed: " + ex.Message); }
        }
    }
    public void Dispose() => process?.Dispose();
}
