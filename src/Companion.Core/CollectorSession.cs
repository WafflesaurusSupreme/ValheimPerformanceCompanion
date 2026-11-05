namespace Companion.Core;

internal interface ICollectorSession : IDisposable
{
    void Validate();
    Task PrepareAsync(CollectorRequest request);
    Task BeginAsync(CollectorRequest request);
    Task FinishAsync(CollectorRequest request, List<string> diagnostics);
}

internal sealed class CollectorSession : ICollectorSession
{
    private bool prepared;
    public void Validate() => PerfViewCollector.ValidateTool();
    public async Task PrepareAsync(CollectorRequest request)
    {
        CollectorBudget.ValidatePreflight(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(request.Directory))!).AvailableFreeSpace);
        string file = Path.Combine(request.Directory, "collector-request.json");
        await PerfViewCollector.WriteAsync(file, request).ConfigureAwait(false);
        prepared = true; // Retain ownership even if the service accepted Start but its reply was lost.
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var reply = await CollectorServiceClient.SendAsync(request, "start").ConfigureAwait(false);
                if (reply.State != "Ready") throw new IOException("PerfView readiness could not be confirmed.");
                break;
            }
            catch (CollectorServiceException error) when (error.State == "Finalizing" && wait.Elapsed < TimeSpan.FromSeconds(90))
            { await Task.Delay(200).ConfigureAwait(false); }
        }
    }
    public async Task BeginAsync(CollectorRequest request)
    {
        await CollectorServiceClient.SendAsync(request, "begin").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(request.Directory, "collector-begin.signal"), request.SessionId).ConfigureAwait(false);
    }
    public async Task FinishAsync(CollectorRequest request, List<string> diagnostics)
    {
        if (!prepared) return;
        var reply = await CollectorServiceClient.SendAsync(request, "stop").ConfigureAwait(false);
        if (reply.State == "Absent")
        {
            string source = CollectorServiceClient.StagingDirectory(request.SessionId);
            if (Directory.Exists(source)) throw new IOException("Collector state is unavailable but retained session data exists. Finalization is still unconfirmed.");
            prepared = false; return; // No protected session directory means no helper could have been launched.
        }
        await CollectorServiceClient.CopyResultsAsync(request, reply.Directory).ConfigureAwait(false);
        await CollectorServiceClient.SendAsync(request, "release").ConfigureAwait(false);
        prepared = false;
        string receipt = Path.Combine(request.Directory, "collector-result.json");
        var result = File.Exists(receipt) ? System.Text.Json.JsonSerializer.Deserialize<CollectorResult>(await File.ReadAllTextAsync(receipt).ConfigureAwait(false)) : null;
        if (result?.Success != true) diagnostics.Add(result?.Message ?? "Collector did not leave a completion receipt.");
        else if (File.Exists(Path.Combine(request.Directory, "recorder.json")))
        {
            try { string? warning = await PerfViewCollector.ExportStacksAsync(request.Directory, request.GamePid).ConfigureAwait(false); if (warning is not null) diagnostics.Add(warning); }
            catch (Exception ex) { diagnostics.Add("Raw trace retained; automatic CPU stack export failed: " + ex.Message); }
        }
    }
    public void Dispose() { }
}
