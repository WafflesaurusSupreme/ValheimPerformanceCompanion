using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Companion.Core;

string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
int passed = 0;
foreach (long free in new[] { CollectorBudget.PreflightFreeBytes, CollectorBudget.PreflightFreeBytes + 1, long.MaxValue })
{ CollectorBudget.ValidatePreflight(free); passed++; }
foreach (long free in new[] { 0L, CollectorBudget.PreflightFreeBytes - 1 })
{
    try { CollectorBudget.ValidatePreflight(free); throw new Exception("Insufficient preflight space accepted"); }
    catch (IOException) { passed++; }
}
void CheckBudget(long trace, long free, string? expected)
{
    if (CollectorBudget.ResourceStopReason(trace, free) != expected) throw new Exception("Wrong collector resource stop reason");
    passed++;
}
CheckBudget(2L * 1024 * 1024 * 1024 + 1, CollectorBudget.PreflightFreeBytes, null);
CheckBudget(CollectorBudget.MaxTraceBytes - 1, CollectorBudget.MinimumFreeBytes, null);
CheckBudget(CollectorBudget.MaxTraceBytes, CollectorBudget.MinimumFreeBytes, "trace-byte-limit");
CheckBudget(CollectorBudget.MaxTraceBytes + 1, CollectorBudget.MinimumFreeBytes, "trace-byte-limit");
CheckBudget(0, CollectorBudget.MinimumFreeBytes - 1, "free-space-reserve");
CheckBudget(CollectorBudget.MaxTraceBytes, 0, "trace-byte-limit");
string budgetMessage = CollectorBudget.QualityMessage("free-space-reserve", 123, 456);
if (!budgetMessage.Contains("free-space-reserve") || !budgetMessage.Contains("bytes=123") || !budgetMessage.Contains("bytes=456"))
    throw new Exception("Collector quality warning omits observed resource values");
passed++;
Console.WriteLine("PASS collector preflight, old-limit regression, resource boundaries and diagnostic values (12 checks)");
async Task CheckCase(string name, Func<Fixture, Task> test)
{
    await using var fixture = new Fixture(Path.Combine(root, name));
    await test(fixture); passed++; Console.WriteLine("PASS " + name);
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

foreach (string lost in new[] { "start", "prepare", "begin", "stop" })
    await CheckCase("lost-" + lost, async f =>
    {
        f.DropReply = lost;
        await using var controller = new CaptureController(() => f.Collector);
        await controller.ConnectAsync(Environment.ProcessId);
        if (lost == "stop") { await controller.StartAsync("Monitor", 600, "test"); await controller.StopAsync(); }
        else
        {
            try { await controller.StartAsync(lost == "start" ? "Monitor" : "Deep", 600, "test"); throw new Exception("Expected lost reply"); }
            catch (EndOfStreamException) { }
        }
        Check(!controller.Active && f.Status.State == "Idle" && f.Stops == 1, "Owned session was not stopped after ambiguous reply");
        Check(f.WrongStops == 0, "Stopped an unowned session");
    });
await CheckCase("unavailable-reconciliation", async f =>
{
    f.DropReply = "start"; f.DropStatus = true;
    await using var controller = new CaptureController(() => f.Collector);
    await controller.ConnectAsync(Environment.ProcessId);
    try { await controller.StartAsync("Monitor", 600, "test"); throw new Exception("Expected cleanup failure"); }
    catch (AggregateException) { }
    Check(controller.Active, "Lost session identity when recovery was unavailable");
    f.DropStatus = false;
    await controller.StopAsync();
    Check(!controller.Active && f.Stops == 1 && f.Status.State == "Idle", "Stop retry did not recover");
});
await CheckCase("other-session-preserved", async f =>
{
    f.DropReply = "start"; f.ReplaceSession = true;
    await using var controller = new CaptureController(() => f.Collector);
    await controller.ConnectAsync(Environment.ProcessId);
    try { await controller.StartAsync("Monitor", 600, "test"); throw new Exception("Expected lost reply"); }
    catch (EndOfStreamException) { }
    Check(!controller.Active && f.Stops == 0 && f.Status.State == "Recording", "Another session was modified");
});
foreach (string mode in new[] { "Standard", "Deep" })
    await CheckCase("delayed-collector-" + mode, async f =>
    {
        f.Collector.Delay = TimeSpan.FromSeconds(5.2);
        await using var controller = new CaptureController(() => f.Collector);
        await controller.ConnectAsync(Environment.ProcessId);
        await controller.StartAsync(mode, 5, "test");
        Check(f.BeginUtc >= f.Collector.ReadyUtc && f.Collector.ReadyUtc - f.PreparedUtc > TimeSpan.FromSeconds(5), "Measurement began before collector readiness");
        Check(f.Status.State == "Recording" && f.Status.ElapsedSeconds == 0 && f.RequestedSeconds == 5, "Minimum duration consumed by preparation");
        Check(File.Exists(Path.Combine(f.Status.Directory, "collector-begin.signal")), "Helper measurement watchdog not armed");
        try { await controller.StartAsync(mode, 5, "duplicate"); throw new Exception("Duplicate start accepted"); }
        catch (InvalidOperationException) { }
        Check(controller.Active && !f.Collector.Disposed, "Duplicate start detached the current collector");
        await controller.StopAsync();
        Check(f.Collector.Finished && !controller.Active, "Collector was not finalized");
    });
await CheckCase("collector-failure", async f =>
{
    f.Collector.Fail = true;
    await using var controller = new CaptureController(() => f.Collector);
    await controller.ConnectAsync(Environment.ProcessId);
    try { await controller.StartAsync("Deep", 5, "test"); throw new Exception("Expected collector failure"); }
    catch (IOException ex) when (ex.Message == "fixture collector failure") { }
    Check(f.BeginUtc == default && f.Stops == 1 && f.Collector.Finished && !controller.Active, "Collector failure left preparation active");
});
await CheckCase("old-recorder", async f =>
{
    f.Status.SupportsPreparation = false;
    await using var controller = new CaptureController(() => f.Collector);
    await controller.ConnectAsync(Environment.ProcessId);
    try { await controller.StartAsync("Standard", 5, "test"); throw new Exception("Expected version guard"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("update", StringComparison.Ordinal)) { }
    Check(!controller.Active && f.Status.State == "Idle", "Older recorder started unsupported capture");
});
foreach (string value in new[] { "[]", "null", "true", "42", "\"text\"", "{broken" })
{
    string folder = Path.Combine(root, "metadata-" + passed); Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "frames.csv"), "UtcTimestamp,ElapsedMs,FrameIntervalMs\n2026-09-27T12:00:00Z,0,0\n2026-09-27T12:00:00.016Z,16,16\n2026-09-27T12:00:00.032Z,32,16\n");
    File.WriteAllText(Path.Combine(folder, "configuration.json"), value);
    var capture = CaptureReader.Read(folder);
    Check(capture.Frames.Count == 3 && capture.Warnings.Any(w => w.StartsWith("configuration.json:", StringComparison.Ordinal)), "Optional metadata blocked recovery or omitted warning");
    passed++;
}
Console.WriteLine($"RESULT {passed} recovery/timing/metadata checks passed");

sealed class FakeCollector : ICollectorSession
{
    internal TimeSpan Delay;
    internal bool Fail, Finished, Disposed;
    internal DateTimeOffset ReadyUtc;
    public void Validate() { }
    public async Task PrepareAsync(CollectorRequest request)
    {
        await Task.Delay(Delay);
        if (Fail) throw new IOException("fixture collector failure");
        ReadyUtc = DateTimeOffset.UtcNow;
    }
    public Task FinishAsync(CollectorRequest request, List<string> diagnostics) { Finished = true; return Task.CompletedTask; }
    public void Dispose() => Disposed = true;
}
sealed class Fixture : IAsyncDisposable
{
    internal readonly RecorderStatus Status;
    internal readonly FakeCollector Collector = new();
    internal string DropReply = "";
    internal bool DropStatus, ReplaceSession;
    internal int Stops, WrongStops, RequestedSeconds;
    internal DateTimeOffset PreparedUtc, BeginUtc;
    private readonly string root;
    private readonly CancellationTokenSource cancel = new(TimeSpan.FromSeconds(40));
    private readonly Task server;
    internal Fixture(string root)
    {
        this.root = root; Directory.CreateDirectory(root);
        string bep = Path.Combine(root, "BepInEx"); Directory.CreateDirectory(bep); File.WriteAllText(Path.Combine(bep, "LogOutput.log"), "fixture\n");
        Status = new() { Protocol = 1, ProcessId = Environment.ProcessId, State = "Idle", BepInExRoot = bep, GameDirectory = root, SupportsPreparation = true };
        server = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + Environment.ProcessId, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                string[] parts = (await RecorderClient.ReadBoundedAsync(reader, 2048, cancel.Token)).Split('\t');
                if (parts[0] is "start" or "prepare")
                {
                    Status.SessionId = parts[1]; Status.Directory = Path.Combine(root, parts[1]); Directory.CreateDirectory(Status.Directory);
                    Status.State = parts[0] == "prepare" ? "Prepared" : "Recording"; RequestedSeconds = int.Parse(parts[3]); PreparedUtc = DateTimeOffset.UtcNow;
                    if (ReplaceSession) Status.SessionId = Guid.NewGuid().ToString("N");
                }
                if (parts[0] == "begin") { BeginUtc = DateTimeOffset.UtcNow; Status.State = "Recording"; }
                if (parts[0] == "stop")
                {
                    if (parts[1] != Status.SessionId) WrongStops++;
                    else { Stops++; Status.State = "Idle"; }
                }
                if (parts[0] == DropReply) { DropReply = ""; continue; }
                if (parts[0] == "status" && DropStatus) continue;
                await writer.WriteLineAsync(JsonSerializer.Serialize(Status));
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { cancel.Cancel(); await server; cancel.Dispose(); }
}
