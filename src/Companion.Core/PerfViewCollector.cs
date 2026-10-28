using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Companion.Core;

public sealed record CollectorRequest(string SessionId, string Directory, int GamePid, int OwnerPid, int Seconds, string Mode);
public sealed record CollectorResult(bool Success, string Message, DateTimeOffset Utc, string? Trace = null);

public static class PerfViewCollector
{
    public const string ExpectedHash = "84B8523F7FB4783FD0BAAE6B080ADB1B5AAC388192145AD713E504C69954556D";
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "Tools", "PerfView.exe");
    public static void ValidateTool()
    {
        if (!File.Exists(Executable)) throw new FileNotFoundException("Bundled PerfView 3.2.6 is missing. Restore the Tools folder from the release package.", Executable);
        using var stream = File.OpenRead(Executable);
        if (Convert.ToHexString(SHA256.HashData(stream)) != ExpectedHash) throw new InvalidDataException("PerfView integrity check failed; the executable differs from the verified release.");
    }
    public static async Task<int> RunHelperAsync(string requestFile)
    {
        CollectorRequest? request = null;
        bool started = false;
        try
        {
            if (new FileInfo(requestFile).Length > 16384) throw new InvalidDataException("Collector request is too large.");
            request = JsonSerializer.Deserialize<CollectorRequest>(await File.ReadAllTextAsync(requestFile).ConfigureAwait(false)) ?? throw new InvalidDataException("No collector request.");
            if (!Guid.TryParseExact(request.SessionId, "N", out _) || request.Seconds is < 5 or > 600 || request.Mode is not ("Standard" or "Deep"))
                throw new InvalidDataException("Invalid collector parameters.");
            string directory = Path.GetFullPath(request.Directory);
            if (!Directory.Exists(directory) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(requestFile)), directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Collector output does not match its existing session directory.");
            using Process game = Process.GetProcessById(request.GamePid);
            using Process owner = Process.GetProcessById(request.OwnerPid);
            if (!game.ProcessName.Equals("valheim", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Selected process is not Valheim.");
            DateTime gameStart = game.StartTime, ownerStart = owner.StartTime;
            ValidateTool();
            var drive = new DriveInfo(Path.GetPathRoot(directory)!);
            CollectorBudget.ValidatePreflight(drive.AvailableFreeSpace);
            string session = "VPC-" + request.SessionId;
            string trace = Path.Combine(directory, "cpu.etl");
            string[] common = ["/AcceptEula", "/NoGui", "/SessionName:" + session];
            string kernel = request.Mode == "Deep" ? "Process,Thread,ImageLoad,Profile,DiskIO,DiskFileIO,ContextSwitch" : "Process,Thread,ImageLoad,Profile";
            await RunAsync(common.Concat(["/LogFile:" + Path.Combine(directory, "perfview-start.log"), "/DataFile:" + trace,
                "/CircularMB:0", "/BufferSizeMB:64", "/CpuSampleMSec:2", "/KernelEvents:" + kernel,
                "/ClrEvents:Loader,Jit", "/FocusProcess:" + request.GamePid.ToString(CultureInfo.InvariantCulture), "start"]), 60).ConfigureAwait(false);
            started = true;
            await WriteAsync(Path.Combine(directory, "collector-ready.json"), new CollectorResult(true, "PerfView collection started. ETW is machine-wide; analysis focuses on the selected game.", DateTimeOffset.UtcNow)).ConfigureAwait(false);
            var preparation = Stopwatch.StartNew();
            Stopwatch? elapsed = null;
            string endReason;
            long traceBytes = 0, freeBytes = 0;
            while (true)
            {
                traceBytes = Directory.GetFiles(directory, "*.etl").Sum(p => new FileInfo(p).Length);
                freeBytes = drive.AvailableFreeSpace;
                if (File.Exists(Path.Combine(directory, "collector-stop.signal"))) { endReason = "stop-requested"; break; }
                if (File.Exists(Path.Combine(directory, "completed.json"))) { endReason = "recorder-completed"; break; }
                string? resourceReason = CollectorBudget.ResourceStopReason(traceBytes, freeBytes);
                if (resourceReason is not null)
                {
                    endReason = resourceReason;
                    await File.WriteAllTextAsync(Path.Combine(directory, "collector-quality.txt"), CollectorBudget.QualityMessage(endReason, traceBytes, freeBytes)).ConfigureAwait(false);
                    break;
                }
                if (game.HasExited) { endReason = "game-exited"; break; }
                if (owner.HasExited) { endReason = "owner-exited"; break; }
                if (game.StartTime != gameStart || owner.StartTime != ownerStart) { endReason = "process-identity-changed"; break; }
                if (elapsed is null && File.Exists(Path.Combine(directory, "collector-begin.signal"))) elapsed = Stopwatch.StartNew();
                if (elapsed is null ? preparation.Elapsed.TotalSeconds >= 120 : elapsed.Elapsed.TotalSeconds >= request.Seconds + 15)
                { endReason = elapsed is null ? "preparation-timeout" : "duration-watchdog"; break; }
                await Task.Delay(250).ConfigureAwait(false);
            }
            await WriteAsync(Path.Combine(directory, "collector-end.json"), new
            {
                EndReason = endReason, TraceBytes = traceBytes, FreeBytes = freeBytes,
                ElapsedSeconds = elapsed?.Elapsed.TotalSeconds, PreparationSeconds = preparation.Elapsed.TotalSeconds,
                RequestedSeconds = request.Seconds, CollectorBudget.MaxTraceBytes,
                CollectorBudget.MinimumFreeBytes, CollectorBudget.PreflightFreeBytes, Utc = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);
            await RunAsync(common.Concat(["/LogFile:" + Path.Combine(directory, "perfview-stop.log"), "/DataFile:" + trace, "stop"]), 120).ConfigureAwait(false);
            started = false;
            string? result = new[] { trace + ".zip", trace }.FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length > 1024);
            if (result is null) throw new IOException("PerfView stopped but no nonempty trace was produced.");
            await WriteAsync(Path.Combine(directory, "collector-result.json"), new CollectorResult(true, "Trace captured. CPU stack export follows; runtime symbol coverage may be incomplete.", DateTimeOffset.UtcNow, Path.GetFileName(result))).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            if (request is not null && Directory.Exists(request.Directory))
                await WriteAsync(Path.Combine(request.Directory, "collector-result.json"), new CollectorResult(false, ex.Message, DateTimeOffset.UtcNow)).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (started && request is not null)
            {
                try { await RunAsync(["/AcceptEula", "/NoGui", "/SessionName:VPC-" + request.SessionId,
                    "/LogFile:" + Path.Combine(request.Directory, "perfview-cleanup.log"), "/DataFile:" + Path.Combine(request.Directory, "cpu.etl"), "stop"], 90).ConfigureAwait(false); }
                catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(request.Directory, "collector-cleanup-error.txt"), ex.Message).ConfigureAwait(false); }
            }
        }
    }
    public static async Task<string?> ExportStacksAsync(string directory, int pid, CancellationToken cancellation = default)
    {
        TraceSummary summary = await Task.Run(() => TraceAnalysis.Generate(directory, pid, cancellation), cancellation).ConfigureAwait(false);
        return summary.EventsLost > 0 || summary.MissingStacks > 0
            ? $"CPU trace: {summary.EventsLost} lost events; {summary.MissingStacks}/{summary.Samples} samples have no stack. Native frame names may be incomplete; the raw trace was retained." : null;
    }
    internal static async Task RunAsync(IEnumerable<string> arguments, int seconds, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("PerfView did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new TimeoutException("PerfView command timed out; inspect its capture log."); }
        if (process.ExitCode != 0) throw new IOException($"PerfView exited with code {process.ExitCode}; inspect its capture log.");
    }
    public static async Task WriteAsync<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        File.Move(temporary, path, true); // Readers must never observe a partly written readiness receipt.
    }
}
