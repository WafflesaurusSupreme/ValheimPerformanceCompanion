using System.Diagnostics;
using System.Text.Json;
using Companion.Core;

namespace Companion.Collector;

internal sealed class CollectorServer : IAsyncDisposable
{
    private sealed record Job(Caller Owner, ServiceRequest Request, string Directory, Process Process);
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);

    internal async Task RunAsync(CancellationToken cancellation)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ValheimPerformanceCompanion");
        ServiceSecurity.CreateProtectedDirectory(root);
        ServiceSecurity.CreateProtectedDirectory(Path.Combine(root, "Collector"));
        await using var pipe = ServiceSecurity.CreatePipe();
        while (!cancellation.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(160));
            try
            {
                ServiceReply reply;
                try
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    ServiceRequest request = await CollectorProtocol.ReadAsync<ServiceRequest>(pipe, readTimeout.Token).ConfigureAwait(false);
                    Caller caller = ServiceSecurity.Authenticate(pipe);
                    CollectorProtocol.Validate(request);
                    reply = await HandleAsync(caller, request, requestTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
                {
                    if (cancellation.IsCancellationRequested) break;
                    reply = new ServiceReply(false, error.Message, "Error");
                }
                try
                {
                    using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    replyTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await CollectorProtocol.WriteReplyAsync(pipe, reply, replyTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception replyError) when (replyError is IOException or OperationCanceledException)
                { System.Diagnostics.Trace.TraceWarning(replyError.Message); }
            }
            finally { ServiceSecurity.Disconnect(pipe); }
        }
    }

    private async Task<ServiceReply> HandleAsync(Caller caller, ServiceRequest request, CancellationToken cancellation)
    {
        if (request.OwnerPid != caller.Pid) throw new UnauthorizedAccessException("Capture owner does not match the requesting process.");
        ReapExitedJobs();
        if (!jobs.TryGetValue(request.SessionId, out Job? job))
        {
            if (request.Command is "stop" or "release" or "status") return await RecoverRetainedAsync(caller, request, cancellation).ConfigureAwait(false);
            if (request.Command != "start") throw new InvalidOperationException("The profiling session has not been prepared.");
            ServiceSecurity.ValidateGame(caller, request);
            if (jobs.Count >= 8 || jobs.Values.Any(j => !j.Process.HasExited))
            {
                bool abandoned = jobs.Values.Any(j => !j.Process.HasExited && !ServiceSecurity.OwnerIsAlive(j.Owner));
                return new ServiceReply(false, abandoned ? "A previous collector is finalizing after its desktop exited." : "The profiling collector is already in use.", abandoned ? "Finalizing" : "Busy");
            }
            string directory = CollectorProtocol.SessionDirectory(caller.Sid, request.SessionId);
            if (System.IO.Directory.Exists(directory)) throw new IOException("A collector session directory already exists; it cannot be reused.");
            ServiceSecurity.CreateProtectedDirectory(Path.GetDirectoryName(directory)!, caller.Sid);
            ServiceSecurity.CreateProtectedDirectory(directory, caller.Sid);
            string file = Path.Combine(directory, "collector-request.json");
            await PerfViewCollector.WriteAsync(file, new CollectorRequest(request.SessionId, directory, request.GamePid, caller.Pid, request.Seconds, request.Mode)).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ValheimPerformanceCompanion.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--collector"); start.ArgumentList.Add(file);
            Process process = Process.Start(start) ?? throw new IOException("The installed profiling helper could not be started.");
            job = new Job(caller, request, directory, process);
            jobs.Add(request.SessionId, job);
        }
        if (job.Owner != caller || job.Request.GamePid != request.GamePid || job.Request.Seconds != request.Seconds || job.Request.Mode != request.Mode)
            throw new UnauthorizedAccessException("The profiling session belongs to a different process or request.");
        switch (request.Command)
        {
            case "start":
                var readiness = Stopwatch.StartNew();
                string readyFile = Path.Combine(job.Directory, "collector-ready.json");
                while (!File.Exists(readyFile))
                {
                    if (job.Process.HasExited || readiness.Elapsed > TimeSpan.FromSeconds(65))
                        throw new IOException("The profiling collector did not become ready.");
                    await Task.Delay(100, cancellation).ConfigureAwait(false);
                }
                CollectorResult? ready = JsonSerializer.Deserialize<CollectorResult>(await File.ReadAllTextAsync(readyFile, cancellation).ConfigureAwait(false));
                if (ready?.Success != true || job.Process.HasExited) throw new IOException("The profiling collector failed to start.");
                return new ServiceReply(true, "Profiling collector ready.", "Ready");
            case "begin":
                if (job.Process.HasExited) throw new IOException("The profiling collector stopped before measurement began.");
                await File.WriteAllTextAsync(Path.Combine(job.Directory, "collector-begin.signal"), request.SessionId, cancellation).ConfigureAwait(false);
                return new ServiceReply(true, "Profiling measurement started.", "Recording");
            case "stop":
                if (!job.Process.HasExited)
                {
                    await File.WriteAllTextAsync(Path.Combine(job.Directory, "collector-stop.signal"), request.SessionId, cancellation).ConfigureAwait(false);
                    using var finishTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    finishTimeout.CancelAfter(TimeSpan.FromSeconds(145));
                    await job.Process.WaitForExitAsync(finishTimeout.Token).ConfigureAwait(false);
                }
                return new ServiceReply(true, "Profiling collector stopped.", "Stopped", job.Directory);
            case "status":
                return new ServiceReply(true, "Profiling collector status.", job.Process.HasExited ? "Stopped" : "Running");
            case "release":
                if (!job.Process.HasExited) throw new IOException("A running profiling session cannot be released.");
                // Retain traces until explicit capture cleanup is authorized; a lost reply must never destroy the only copy.
                jobs.Remove(request.SessionId); job.Process.Dispose();
                return new ServiceReply(true, "Profiling session released; its protected staging copy is retained.", "Released");
            default: throw new InvalidDataException("Unknown profiling collector command.");
        }
    }

    private void ReapExitedJobs()
    {
        foreach (var entry in jobs.Where(entry => entry.Value.Process.HasExited).ToArray())
        {
            jobs.Remove(entry.Key);
            entry.Value.Process.Dispose(); // Release only the process/index entry; retained trace files are untouched.
        }
    }

    private static async Task<ServiceReply> RecoverRetainedAsync(Caller caller, ServiceRequest request, CancellationToken cancellation)
    {
        string directory = CollectorProtocol.SessionDirectory(caller.Sid, request.SessionId);
        if (!System.IO.Directory.Exists(directory)) return new ServiceReply(true, "No collector session was started.", "Absent");
        ServiceSecurity.ValidateStagingDirectory(directory);
        var expected = new CollectorRequest(request.SessionId, directory, request.GamePid, caller.Pid, request.Seconds, request.Mode);
        var stored = CollectorStaging.ReadReceipt<CollectorRequest>(Path.Combine(directory, "collector-request.json"));
        CollectorStaging.ValidateBinding(stored, expected, directory);
        string resultFile = Path.Combine(directory, "collector-result.json");
        if (request.Command == "status") return new ServiceReply(true, "Retained collector session.", File.Exists(resultFile) ? "Stopped" : "Finalizing");
        if (request.Command == "release")
        {
            CollectorStaging.ReadReceipt<CollectorResult>(resultFile);
            return new ServiceReply(true, "Retained session no longer occupies a collector slot.", "Released");
        }
        if (!File.Exists(resultFile))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "collector-stop.signal"), request.SessionId, cancellation).ConfigureAwait(false);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(resultFile))
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(145)) throw new IOException("Collector finalization remains unconfirmed; retained traces are preserved for retry.");
                await Task.Delay(150, cancellation).ConfigureAwait(false);
            }
        }
        CollectorStaging.ReadReceipt<CollectorResult>(resultFile);
        return new ServiceReply(true, "Recovered finalized trace from protected staging.", "Stopped", directory);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Job job in jobs.Values)
            if (!job.Process.HasExited)
                await File.WriteAllTextAsync(Path.Combine(job.Directory, "collector-stop.signal"), job.Request.SessionId).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        foreach (Job job in jobs.Values)
        {
            try { if (!job.Process.HasExited) await job.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { System.Diagnostics.Trace.TraceWarning("Profiling helper is still finalizing; its own bounded watchdog remains active."); }
            finally { job.Process.Dispose(); }
        }
    }
}
