using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Companion.Core;

public sealed class CaptureController : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private CancellationTokenSource? serverStop;
    private Task? server;
    private ICollectorSession? collector;
    private CollectorRequest? collectorRequest;
    private readonly Func<ICollectorSession> createCollector;
    public CaptureController() : this(() => new CollectorSession()) { }
    internal CaptureController(Func<ICollectorSession> createCollector) => this.createCollector = createCollector;
    private Process? game;
    private long logStart;
    private string? logPath;
    private string sessionId = "";
    public RecorderStatus? Status { get; private set; }
    public string? SessionDirectory { get; private set; }
    public bool Active { get; private set; }
    public int? Pid { get; private set; }
    public Func<string, Task<string>>? ConsoleCommand { get; set; }
    public List<string> Diagnostics { get; } = [];

    public Task<RecorderStatus> ConnectAsync(int pid) => ConnectAsync(pid, null);
    public async Task<RecorderStatus> ConnectAsync(int pid, GameTarget? expectedTarget)
    {
        if (Active) throw new InvalidOperationException("Stop the current recording before changing game instances.");
        RecorderStatus hello = await RecorderClient.SendAsync(pid, "hello").ConfigureAwait(false);
        if (expectedTarget is not null)
        {
            static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            if (!SamePath(hello.GameDirectory, expectedTarget.GameDirectory)
                || (expectedTarget.ProfileDirectory is not null && !SamePath(hello.BepInExRoot, expectedTarget.BepInExRoot)))
                throw new InvalidOperationException("The running recorder belongs to a different game/profile. Select the running profile to connect.");
        }
        if (hello.State != "Idle") throw new InvalidOperationException("The recorder is already recording or saving. Wait for its bounded capture to finish before connecting.");
        if (serverStop is not null) { serverStop.Cancel(); if (server is not null) await server.ConfigureAwait(false); serverStop.Dispose(); }
        game?.Dispose(); game = Process.GetProcessById(pid); Pid = pid; Status = hello;
        serverStop = new CancellationTokenSource(); server = ListenConsoleAsync(pid, serverStop.Token);
        return hello;
    }
    public async Task StartAsync(string mode, int duration, string scenario)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        bool attempted = false;
        bool createdCollector = false;
        try
        {
            if (Active || Pid is null || Status is null || game?.HasExited != false) throw new InvalidOperationException("Connect to an idle recorder in a running Valheim instance first.");
            if (mode is not ("Monitor" or "Standard" or "Deep") || duration is < 5 or > 600) throw new ArgumentException("Choose Monitor, Standard or Deep and a duration from 5 to 600 seconds.");
            if (mode != "Monitor" && !Status.SupportsPreparation) throw new InvalidOperationException("This recorder needs an update for synchronized capture. Close Valheim so automatic setup can install the bundled recorder, then relaunch your profile.");
            Diagnostics.Clear(); sessionId = Guid.NewGuid().ToString("N"); SessionDirectory = null;
            if (mode != "Monitor") { collector = createCollector(); createdCollector = true; collector.Validate(); }
            logPath = Path.Combine(Status.BepInExRoot, "LogOutput.log"); logStart = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
            // Retain ownership before crossing IPC: a missing reply does not mean the request failed.
            Active = true; attempted = true;
            Status = await RecorderClient.SendAsync(Pid.Value, $"{(mode == "Monitor" ? "start" : "prepare")}\t{sessionId}\t{mode}\t{duration}").ConfigureAwait(false);
            AdoptOwnedStatus(Status);
            if (mode != "Monitor")
            {
                if (Status.State != "Prepared") throw new IOException("Recorder did not enter preparation state.");
                collectorRequest = new(sessionId, SessionDirectory!, Pid.Value, Environment.ProcessId, duration, mode);
                await collector!.PrepareAsync(collectorRequest).ConfigureAwait(false);
                Status = await RecorderClient.SendAsync(Pid.Value, "begin\t" + sessionId).ConfigureAwait(false);
                AdoptOwnedStatus(Status);
                // The helper's measurement watchdog starts only after the recorder's Begin acknowledgement.
                await File.WriteAllTextAsync(Path.Combine(SessionDirectory!, "collector-begin.signal"), sessionId).ConfigureAwait(false);
            }
            if (Status.State != "Recording") throw new IOException("The recorder did not confirm an active measurement.");
            await PerfViewCollector.WriteAsync(Path.Combine(SessionDirectory!, "session.json"), new {
                Schema = 1, SessionId = sessionId, ProcessId = Pid, GameStartUtc = game.StartTime.ToUniversalTime(),
                Mode = mode, Scenario = scenario, RecorderConfirmedUtc = DateTimeOffset.UtcNow,
                Timing = "Recorder preparation and PerfView readiness precede the measurement interval. Recorder and ETW retain their own actual timestamps."
            }).ConfigureAwait(false);
        }
        catch (Exception startError)
        {
            if (attempted)
            {
                try { await StopInternalAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) { throw new AggregateException("Capture start failed; cleanup is unconfirmed. Click Stop to retry the owned session.", startError, cleanupError); }
            }
            else if (createdCollector) { collector?.Dispose(); collector = null; }
            throw;
        }
        finally { gate.Release(); }
    }
    private void AdoptOwnedStatus(RecorderStatus status)
    {
        if (status.SessionId != sessionId) throw new IOException("The recorder returned another session; it has not been modified.");
        if (string.IsNullOrWhiteSpace(status.Directory)) throw new InvalidDataException("Recorder session directory is missing.");
        SessionDirectory = status.Directory;
    }
    public async Task<RecorderStatus?> PollAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Pid is null) return null;
            Status = await RecorderClient.SendAsync(Pid.Value, "status").ConfigureAwait(false);
            if (Active && Status.SessionId == sessionId) AdoptOwnedStatus(Status);
            return Status;
        }
        finally { gate.Release(); }
    }
    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopInternalAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task StopInternalAsync()
    {
        if (!Active || Pid is null) return;
        Exception? pending = null;
        bool resolved = false;
        try
        {
            if (game?.HasExited == true) { resolved = true; Diagnostics.Add("Valheim exited; partial capture files are preserved."); }
            else
            {
                // Reconcile first, including ambiguous Start/Prepare/Begin replies. Never stop another session.
                Status = await RecorderClient.SendAsync(Pid.Value, "status").ConfigureAwait(false);
                if (Status.SessionId != sessionId) { resolved = true; Diagnostics.Add("The attempted session is no longer active; another session was left untouched."); }
                else
                {
                    AdoptOwnedStatus(Status);
                    if (Status.State is "Recording" or "Prepared")
                    {
                        try { Status = await RecorderClient.SendAsync(Pid.Value, "stop\t" + sessionId).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException)
                        { Status = await RecorderClient.SendAsync(Pid.Value, "status").ConfigureAwait(false); }
                    }
                    var wait = Stopwatch.StartNew();
                    while (Status.SessionId == sessionId && Status.State == "Saving" && wait.Elapsed.TotalSeconds < 45)
                    { await Task.Delay(150).ConfigureAwait(false); Status = await RecorderClient.SendAsync(Pid.Value, "status").ConfigureAwait(false); }
                    resolved = Status.SessionId != sessionId || Status.State == "Idle";
                    if (!resolved) throw new IOException("Recorder cleanup is not confirmed. Click Stop to retry; the session identity is retained.");
                    if (Status.LastError.Length > 0) Diagnostics.Add(Status.LastError);
                }
            }
        }
        catch (Exception ex) { pending = ex; Diagnostics.Add("Recorder cleanup unconfirmed: " + ex.Message); }
        try
        {
            if (collector is not null && collectorRequest is not null) await collector.FinishAsync(collectorRequest, Diagnostics).ConfigureAwait(false);
            if (resolved && SessionDirectory is not null)
            {
                CopyLogTail();
                await File.WriteAllLinesAsync(Path.Combine(SessionDirectory, "capture-health.txt"), Diagnostics).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { pending = pending is null ? ex : new AggregateException(pending, ex); }
        finally
        {
            Active = !resolved;
            collector?.Dispose(); collector = null; collectorRequest = null;
        }
        if (pending is not null) throw new IOException("Capture cleanup: " + pending.Message, pending);
    }
    private void CopyLogTail()
    {
        if (logPath is null || SessionDirectory is null || !File.Exists(logPath)) { Diagnostics.Add("Game log unavailable."); return; }
        try
        {
            using var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length < logStart) { Diagnostics.Add("Game log rotated or truncated during capture; copied the available log."); logStart = 0; }
            input.Position = logStart;
            using var output = new FileStream(Path.Combine(SessionDirectory, "game-log.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            byte[] buffer = new byte[65536]; long remaining = Math.Min(input.Length - logStart, 64L * 1024 * 1024);
            while (remaining > 0) { int read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length)); if (read == 0) break; output.Write(buffer, 0, read); remaining -= read; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Add("Game log copy failed: " + ex.Message); }
    }
    private async Task ListenConsoleAsync(int pid, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Controller." + pid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(180));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                string command = await RecorderClient.ReadBoundedAsync(reader, 512, timeout.Token).ConfigureAwait(false);
                string response;
                try { response = ConsoleCommand is null ? "Controller is not ready." : await ConsoleCommand(command).ConfigureAwait(false); }
                catch (Exception ex) { response = "Request failed: " + ex.Message; }
                await writer.WriteLineAsync(response.Replace('\n', ' ').AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { if (!cancellation.IsCancellationRequested) Diagnostics.Add("Console connection: " + ex.Message); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        serverStop?.Cancel();
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            try { if (server is not null) await server.ConfigureAwait(false); }
            finally { game?.Dispose(); serverStop?.Dispose(); gate.Dispose(); }
        }
    }
}
