using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Companion.Core;

if (args.Length > 0 && args[0] == "--workload")
{
    var duration = Stopwatch.StartNew(); long value = 0;
    while (duration.Elapsed.TotalSeconds < 45) { for (int i = 0; i < 100000; i++) value ^= (long)Math.Sqrt(i + value % 100 + 100); Thread.Sleep(2); }
    System.Console.WriteLine(value); return;
}
if (args.Length == 3 && args[0] == "--export-check")
{
    int pid = int.Parse(args[2], CultureInfo.InvariantCulture);
    string? warning = await PerfViewCollector.ExportStacksAsync(args[1], pid);
    var rows = Csv.Read(Path.Combine(args[1], "cpu-samples.csv")).ToArray();
    File.WriteAllText(Path.Combine(args[1], "export-verification.json"), JsonSerializer.Serialize(new { ProcessId = pid, Samples = rows.Length, Warning = warning }, new JsonSerializerOptions { WriteIndented = true }));
    System.Console.WriteLine($"CPU_EXPORT_VALIDATED pid={pid} samples={rows.Length}; {warning}"); return;
}
string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "ValheimCompanionTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
int passed = 0, failed = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
void Test(string name, Action body)
{
    try { body(); passed++; System.Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; System.Console.WriteLine("FAIL " + name + ": " + ex); }
}
async Task TestAsync(string name, Func<Task> body)
{
    try { await body(); passed++; System.Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; System.Console.WriteLine("FAIL " + name + ": " + ex); }
}
Capture Make(params double[] intervals)
{
    var value = new Capture { Name = "Fixture", StartUtc = DateTimeOffset.Parse("2026-09-27T12:00:00Z", CultureInfo.InvariantCulture) };
    double elapsed = 0;
    foreach (double interval in intervals) { elapsed += interval / 1000; value.Frames.Add(new Frame(elapsed, interval, value.StartUtc.AddSeconds(elapsed)) { Index = value.Frames.Count, Focused = true, HasPlayer = true }); }
    return value;
}
string Fixture(string name, string csv)
{
    string folder = Path.Combine(output, name); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "frames.csv"), csv); return folder;
}
const string header = "UtcTimestamp,ElapsedMs,FrameIntervalMs,UnityUnscaledDeltaMs,Fps\n";
const string csv = header + "2026-09-27T12:00:00Z,0,0,0,0\n2026-09-27T12:00:00.010Z,10,10,10,100\n2026-09-27T12:00:00.040Z,40,30,30,33.3333\n";

Test("Average FPS uses total intervals, not arithmetic instantaneous FPS", () =>
{ var a = Analyzer.Analyze(Make(10, 30), new()); Check(Math.Abs(a.Metrics.AverageFps - 50) < 1e-9, "Expected 50 FPS"); });
Test("Percentiles and 1% low have defined boundary behavior", () =>
{ var a = Analyzer.Analyze(Make(10, 20, 30, 100), new()); Check(a.Metrics.OnePercentLow == 10 && a.Metrics.WorstMs == 100, "Tail calculation"); Check(Math.Abs(a.Metrics.P99Ms - 97.9) < .0001, "Linear percentile"); });
Test("Focus and menu exclusions preserve unknown-state frames", () =>
{ Capture c = Make(10, 20, 30, 40); c.Frames[0].Focused = false; c.Frames[1].HasPlayer = false; c.Frames[2].Focused = null; var a = Analyzer.Analyze(c, new()); Check(a.Metrics.Frames == 2 && a.Metrics.ExcludedFrames == 2, "State exclusion"); });
Test("Empty selection is explicit", () =>
{ var a = Analyzer.Analyze(Make(10), new(StartSeconds: 10, EndSeconds: 11)); Check(a.Metrics.Frames == 0 && a.Findings[0].Title.StartsWith("No frames", StringComparison.Ordinal), "Empty selection"); });
Test("Relative spikes reveal high-FPS microstutter", () =>
{ var values = Enumerable.Repeat(4.0, 100).Append(20).ToArray(); var a = Analyzer.Analyze(Make(values), new()); Check(a.Metrics.RelativeSpikes == 1 && a.Metrics.LagSpikes == 0, "Relative threshold"); });
Test("Invalid frame budget rejected", () =>
{ try { Analyzer.Analyze(Make(10), new(double.NaN)); throw new Exception("Accepted NaN"); } catch (ArgumentException) { } });
Test("Lag spikes are located automatically without player markers", () =>
{
    var c = Make(Enumerable.Repeat(4.0, 60).Concat(new[] { 20.0, 100.0 }).ToArray());
    var a = Analyzer.Analyze(c, new());
    Check(c.Events.Count == 0 && a.DetectedSpikes.Count == 2, "Detection requires no player events");
    Check(a.DetectedSpikes[0].Relative && !a.DetectedSpikes[0].Large && a.DetectedSpikes[1].Large, "Distinct severity");
    Check(a.DetectedSpikes[1].Frame == c.Frames[^1], "Exact interval retained");
    string report = ReportExporter.Html(a);
    Check(report.Contains("Automatically detected lag spikes", StringComparison.Ordinal) && !report.Contains("hitch", StringComparison.OrdinalIgnoreCase), "Report terminology");
    string archive = Path.Combine(output, "automatic-spikes.zip"); ReportExporter.Export(a, archive, false);
    using var zip = ZipFile.OpenRead(archive); using var reader = new StreamReader(zip.GetEntry("lag-spikes.csv")!.Open());
    Check(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3, "All detected intervals exported");
});
Test("Quoted CSV fields and Unicode paths", () =>
{ string folder = Fixture("路径 [test] O'Brien", csv); File.WriteAllText(Path.Combine(folder, "markers.csv"), "ElapsedMs,Label\n10,\"water, leeches \"\"here\"\"\"\n"); var c = CaptureReader.Read(folder); Check(c.Events.Single().Message == "water, leeches \"here\"", "CSV quoting"); Check(c.Frames.Count == 3, "Frames"); });
Test("Truncated frame row fails explicitly", () =>
{ try { CaptureReader.Read(Fixture("truncated", csv + "2026-09-27T12:00:01Z,1000")); throw new Exception("Accepted partial row"); } catch (InvalidDataException) { } });
Test("Nonfinite and out-of-order frame rows are reported", () =>
{ var c = CaptureReader.Read(Fixture("invalid", csv + "2026-09-27T12:00:01Z,1000,NaN,10,100\n2026-09-27T12:00:00.005Z,5,10,10,100\n")); Check(c.Frames.Count == 3 && c.Warnings.Any(w => w.Contains("Skipped 2", StringComparison.Ordinal)), "Invalid rows warning"); });
Test("Incomplete capture receipt is surfaced", () =>
{ string folder = Fixture("incomplete", csv); File.WriteAllText(Path.Combine(folder, "recorder.json"), "{\"Protocol\":1}"); var c = CaptureReader.Read(folder); Check(c.Warnings.Any(w => w.Contains("no completion receipt", StringComparison.Ordinal)), "Incomplete warning"); });
Test("Interrupted companion capture recovers valid rows before a partial tail", () =>
{ string folder = Fixture("crash-tail", csv + "2026-09-27T12:00:01Z,1000"); File.WriteAllText(Path.Combine(folder, "recorder.json"), "{\"Protocol\":1}"); var c = CaptureReader.Read(folder); Check(c.Frames.Count == 3 && c.Warnings.Any(w => w.Contains("valid prefix", StringComparison.Ordinal)), "Recovery"); });
Test("Nested timings are not summed into causal claims", () =>
{ var c = Make(100); c.Methods.Add(new("Parent", 0, .25, 70, 1, 1)); c.Methods.Add(new("Child", 0, .25, 60, 1, 1)); var a = Analyzer.Analyze(c, new()); Check(a.Findings[0].Confidence == "Associated" && !a.Findings[0].Evidence.Contains("130", StringComparison.Ordinal), "Inclusive interpretation"); });
Test("Aligned CPU evidence remains an association, not a causal claim", () =>
{ var c = Make(100); for (int i = 0; i < 4; i++) c.CpuSamples.Add(new(.02 + i * .002, 1, "world!Generate", "world!Generate")); var a = Analyzer.Analyze(c, new()); Check(a.Findings.Any(f => f.Title == "CPU samples overlap a slow interval" && f.Confidence == "Associated"), "CPU correlation"); });
Test("Structured CPU observations import and preserve timing", () =>
{ string folder = Fixture("cpu-import", csv); File.WriteAllText(Path.Combine(folder, "cpu-samples.csv"), "Seconds,ThreadId,Leaf,Stack\n0.012,55,\"module!method\",\"module!method ← caller\"\n"); var c = CaptureReader.Read(folder); Check(c.CpuSamples.Single().Seconds == .012 && c.CpuSamples.Single().ThreadId == 55, "CPU CSV schema"); });
Test("Steam library discovery supports escaped paths and old format", () =>
{ string[] roots = Discovery.SteamLibraries("\"path\" \"D:\\\\SteamLibrary\"\n\"1\" \"E:\\\\Games\"").ToArray(); Check(roots.SequenceEqual(new[] { @"D:\SteamLibrary", @"E:\Games" }), "VDF libraries"); });
Test("Active profile from Doorstop arguments with spaces and Unicode", () =>
{ string line = "valheim.exe --doorstop-target \"C:\\Mods\\测试 Profile\\BepInEx\\core\\BepInEx.Preloader.dll\" --r2profile whatever"; Check(Discovery.ProfileFromArguments(Discovery.SplitCommandLine(line)) == @"C:\Mods\测试 Profile", "Doorstop identity"); });
Test("No profile guessed from r2profile name alone", () =>
{ Check(Discovery.ProfileFromArguments(["valheim.exe", "--r2profile", "Default"]) == null, "Unverified profile name"); });
Test("Disabled Doorstop target is not reported as an active profile", () =>
{
    Check(Discovery.ProfileFromArguments(["valheim.exe", "--doorstop-enable", "false", "--doorstop-target", @"C:\Mods\Profile\BepInEx\core\BepInEx.Preloader.dll"]) == null, "Disabled loader");
    Check(Discovery.ProfileFromArguments(["valheim.exe", "--doorstop-enabled=false", "--doorstop-target", @"C:\Mods\Profile\BepInEx\core\BepInEx.Preloader.dll"]) == null, "Disabled loader equals syntax");
});
Test("Unzoned log time is never assigned an exact capture instant", () =>
{ string folder = Fixture("logs", csv); File.WriteAllText(Path.Combine(folder, "game-log.log"), "[Error] 12:00:00: Exception one\n[Error] 2026-09-27T12:00:00.010Z Exception two\n"); var c = CaptureReader.Read(folder); Check(c.Events[0].Seconds == null && Math.Abs(c.Events[1].Seconds!.Value - .01) < 1e-8, "Timestamp precision"); });
Test("Report escapes HTML and redacts common identifiers", () =>
{ var c = Make(20); c.Name = "<script>alert(1)</script>"; c.Events.Add(new(null, "Error", @"C:\Users\Alice\logs 192.168.1.2:2456 token=abc", 1)); string html = ReportExporter.Html(Analyzer.Analyze(c, new())); Check(!html.Contains("<script>", StringComparison.Ordinal) && !html.Contains("Alice", StringComparison.Ordinal) && !html.Contains("token=abc", StringComparison.Ordinal), "Safe report"); });
Test("Redacted export excludes raw traces by default", () =>
{ string folder = Fixture("export", csv); File.WriteAllText(Path.Combine(folder, "cpu.etl"), "private"); var a = Analyzer.Analyze(CaptureReader.Read(folder), new()); string destination = Path.Combine(output, "report.zip"); ReportExporter.Export(a, destination, false); using var zip = ZipFile.OpenRead(destination); Check(new[] { "events.csv", "lag-spikes.csv", "metrics.json", "report.html", "diagnostics.json", "CODEX-INVESTIGATION.md", "diagnostic-evidence/frames.csv", "diagnostic-manifest.json" }.All(n => zip.GetEntry(n) != null) && !zip.Entries.Any(e => e.FullName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)), "Diagnostic evidence included; raw ETW opt-in"); });
Test("Full diagnostics export includes original zipped CPU trace", () =>
{
    string folder = Fixture("full-diagnostics", csv);
    string trace = Path.Combine(folder, "cpu.etl.zip");
    using (var source = ZipFile.Open(trace, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(source.CreateEntry("cpu.etl").Open())) writer.Write("test trace");
    string destination = Path.Combine(output, "full-diagnostics.zip");
    ReportExporter.Export(Analyzer.Analyze(CaptureReader.Read(folder), new()), destination, true);
    using var archive = ZipFile.OpenRead(destination);
    var entry = archive.GetEntry("raw/cpu.etl.zip");
    Check(entry is not null && archive.GetEntry("report.html") is not null && archive.GetEntry("raw/frames.csv") is not null, "Trace and report in full diagnostics ZIP");
    using var sourceStream = File.OpenRead(trace);
    using var archiveStream = entry!.Open();
    Check(SHA256.HashData(sourceStream).SequenceEqual(SHA256.HashData(archiveStream)), "Original trace preserved byte for byte");
});
Test("Every spike and repeated collection survives diagnostic analysis", () =>
{
    var c = Make(Enumerable.Range(0, 20).SelectMany(_ => Enumerable.Repeat(10.0, 20).Append(100)).ToArray());
    for (int i = 0; i < c.Frames.Count; i++) c.Frames[i].Gc0 = (i + 1) / 21;
    var a = Analyzer.Analyze(c, new());
    Check(a.Diagnostics.Issues.Count(i => i.Id.StartsWith("frame-", StringComparison.Ordinal)) == 20, "All spikes, including beyond twelve");
    Check(a.Diagnostics.Issues.Single(i => i.Id == "GC-pattern").FrameIndices.Count == 20, "All collections");
});
Test("GC overlap is clipped and unioned; incomplete phases are excluded", () =>
{
    Check(Math.Abs(DiagnosticAnalyzer.UnionOverlap(new[] { (.01, .07), (.03, .09), (.2, .3) }, .02, .08) - .06) < 1e-10, "No double counting");
    var pauses = DiagnosticAnalyzer.Pauses(new[] { new GcPhase(.01, 1, 8, 0), new GcPhase(.02, 1, 7, 0), new GcPhase(.07, 1, 8, 0), new GcPhase(.08, 1, 7, 0) });
    Check(pauses.Count == 1 && pauses[0].Start == .02 && pauses[0].End == .07, "Only complete pairs");
});
Test("Worker activity, patched code and waits remain separate evidence", () =>
{
    var c = Make(100); c.Metadata["MainThreadId"] = "1";
    c.CpuSamples.Add(new(.02, 1, "Game!Thing.Work", "Game!Thing.Work")); c.CpuSamples.Add(new(.03, 2, "Mod!Watch.Scan", "Mod!Watch.Scan"));
    c.Code.Add(new("Game", "a", "1", "Thing", "Work", "Thing.Work()", "", "", "ret", "", ""));
    c.Code.Add(new("Mod", "b", "2", "Patch", "Before", "Patch.Before()", "", "", "ret", "", ""));
    c.Patches.Add(new("Thing.Work()", "Prefix", "mod-owner", "Mod", "Patch.Before", "a", "1", "b", "2"));
    c.Waits.Add(new(.04, .08, 1, "Executive", "Waiting"));
    var a = Analyzer.Analyze(c, new()); var issue = a.Diagnostics.Issues.Single(i => i.Id.StartsWith("frame-", StringComparison.Ordinal));
    Check(issue.Stacks.Any(s => s.ThreadRole == "Main thread") && issue.Stacks.Any(s => s.ThreadRole == "Worker/other thread"), "Thread roles");
    Check(a.Diagnostics.Code.Count == 2 && issue.Evidence.Any(e => e.Contains("40.000 ms", StringComparison.Ordinal)), "Patch IL and wait evidence");
    Check(issue.Status.Contains("requires code review", StringComparison.Ordinal), "No unsupported cause claim");
});
Test("Ambiguous overloads retain candidates and disclose uncertainty", () =>
{
    var c = Make(100); c.CpuSamples.Add(new(.02, 1, "Game!Thing.Work", "Game!Thing.Work"));
    foreach (string token in new[] { "1", "2" }) c.Code.Add(new("Game", "a", token, "Thing", "Work", "Thing.Work", "", "", "ret", "", ""));
    var a = Analyzer.Analyze(c, new()); Check(a.Diagnostics.Code.Count == 2 && a.Diagnostics.Issues[0].Gaps.Any(g => g.Contains("ambiguity", StringComparison.Ordinal)), "Unresolved overload");
});
Test("Sustained slow rendering is reported without a large spike", () =>
{ var a = Analyzer.Analyze(Make(Enumerable.Repeat(25.0, 100).ToArray()), new()); Check(a.Metrics.LagSpikes == 0 && a.Diagnostics.Issues.Any(i => i.Id == "sustained-load"), "Sustained issue"); });
Test("Invalid code bytes and incomplete CPU extent are disclosed", () =>
{
    string folder = Fixture("invalid-code", csv);
    File.WriteAllText(Path.Combine(folder, "code-methods.csv"), "Assembly,Mvid,Token,Type,Method,Signature,IlSha256,IlBase64,Instructions,Locals,ExceptionClauses\nGame,a,1,T,M,T.M,wrong,Kg==,ret,,\n");
    File.WriteAllText(Path.Combine(folder, "cpu-samples.csv"), "Seconds,ThreadId,Leaf,Stack\n0.03,1,Game!T.M,Game!T.M\n");
    var c = CaptureReader.Read(folder); Check(c.Code.Count == 0 && c.Warnings.Any(w => w.Contains("hash mismatch", StringComparison.Ordinal)) && c.Warnings.Any(w => w.Contains("observed sample extent", StringComparison.Ordinal)), "Explicit gaps");
});
Test("Exports never overwrite an existing file", () =>
{ string path = Path.Combine(output, "existing.zip"); File.WriteAllText(path, "keep"); try { ReportExporter.Export(Analyzer.Analyze(Make(10), new()), path, false); throw new Exception("Overwrote file"); } catch (IOException) { Check(File.ReadAllText(path) == "keep", "Preservation"); } });
Test("Graph downsampling preserves isolated peaks", () =>
{ var c = Make(Enumerable.Repeat(10.0, 999).Append(200).ToArray()); Check(ReportExporter.Envelope(c.Frames, 20).Max() == 200, "Peak lost"); });
Test("Comparison flags mode changes", () =>
{ var a = Make(10); var b = Make(20); a.Metadata["Mode"] = "Standard"; b.Metadata["Mode"] = "Deep"; Check(Analyzer.Compare(Analyzer.Analyze(a, new()), Analyzer.Analyze(b, new())).Contains("Recorded differences: Mode", StringComparison.Ordinal), "Metadata comparison"); });
Test("CPU stack XML reads leaf sample weights", () =>
{ string path = Path.Combine(output, "cpu.zip"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("cpu.xml").Open()); writer.Write("<StackSource><Frames><Frame ID='0'>valheim!work</Frame></Frames><Stacks><Stack ID='0' FrameID='0' CallerID='-1'/></Stacks><Samples><Sample StackID='0' Metric='2' TimeRelativeMSec='12'/><Sample StackID='0' Metric='2' TimeRelativeMSec='15'/></Samples></StackSource>"); } var rows = CpuStacks.Read(path); Check(rows.Single().Weight == 4 && rows.Single().Samples == 2, "CPU sample weights"); });
Test("CPU filtering uses stack process identity and actual PerfView Time field", () =>
{
    string path = Path.Combine(output, "cpu-pid.zip");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("cpu.xml").Open()); writer.Write("<StackSource><Frames><Frame ID='0'>work</Frame><Frame ID='1'>Process64 valheim (123) Args:</Frame><Frame ID='2'>Process64 valheim (999) Args:</Frame></Frames><Stacks><Stack ID='0' FrameID='0' CallerID='1'/><Stack ID='1' FrameID='1' CallerID='-1'/><Stack ID='2' FrameID='0' CallerID='3'/><Stack ID='3' FrameID='2' CallerID='-1'/></Stacks><Samples><Sample StackID='0' Metric='2' Time='12'/><Sample StackID='2' Metric='20' Time='12'/><Sample StackID='0' Metric='8' Time='30'/></Samples></StackSource>"); }
    Check(CpuStacks.Read(path, 10, 20, 123).Single().Weight == 2, "Wrong-process or wrong-time samples leaked");
});

await TestAsync("Recorder IPC verifies identity and transfers status", async () =>
{
    int pid = Environment.ProcessId;
    var server = Task.Run(async () =>
    {
        using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(); using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true); using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        Check(await reader.ReadLineAsync() == "hello", "Handshake command"); await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderStatus { Protocol = 1, ProcessId = pid, State = "Idle" }));
    });
    RecorderStatus response = await RecorderClient.SendAsync(pid, "hello"); await server; Check(response.State == "Idle", "Status");
});
await TestAsync("Late attachment rejects the wrong profile before starting a controller", async () =>
{
    int pid = Environment.ProcessId;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var server = Task.Run(async () =>
    {
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await RecorderClient.ReadBoundedAsync(reader, 1024, timeout.Token);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderStatus { Protocol = 1, ProcessId = pid, State = "Idle",
            GameDirectory = output, BepInExRoot = Path.Combine(output, "different-profile", "BepInEx") }));
    });
    await using var controller = new CaptureController();
    bool rejected = false;
    try { await controller.ConnectAsync(pid, new GameTarget(output, Path.Combine(output, "selected-profile"), "fixture", pid)); }
    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("different game/profile", StringComparison.Ordinal); }
    await server;
    Check(rejected && controller.Pid is null && controller.Status is null, "Wrong profile acquired a controller");
});
await TestAsync("Recorder IPC rejects another process identity", async () =>
{
    int pid = Environment.ProcessId;
    var server = Task.Run(async () =>
    {
        using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(); using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true); using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await reader.ReadLineAsync(); await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderStatus { Protocol = 1, ProcessId = pid + 1, State = "Idle" }));
    });
    try { await RecorderClient.SendAsync(pid, "hello"); throw new Exception("Accepted different PID"); } catch (InvalidDataException) { } await server;
});
await TestAsync("Recorder IPC rejects a pipe owned by another process before sending commands", async () =>
{
    int advertisedPid = Environment.ProcessId + 1000000;
    using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + advertisedPid,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Task connected = pipe.WaitForConnectionAsync(timeout.Token);
    try { await RecorderClient.SendAsync(advertisedPid, "hello", timeout.Token); throw new Exception("Accepted another process's endpoint"); }
    catch (InvalidDataException) { }
    await connected;
    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
    Check(await reader.ReadLineAsync(timeout.Token) is null, "Sent a command before verifying server identity");
});
await TestAsync("Controller Monitor lifecycle and duplicate-start preservation", async () =>
{
    string root = Path.Combine(output, "controller"); Directory.CreateDirectory(root);
    string bep = Path.Combine(root, "BepInEx"); Directory.CreateDirectory(bep); File.WriteAllText(Path.Combine(bep, "LogOutput.log"), "before capture\n");
    int pid = Environment.ProcessId; using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var status = new RecorderStatus { Protocol = 1, ProcessId = pid, State = "Idle", BepInExRoot = bep, GameDirectory = root };
    var server = Task.Run(async () =>
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token); using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true); using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                string command = await RecorderClient.ReadBoundedAsync(reader, 2048, cancellation.Token); string[] parts = command.Split('\t');
                if (parts[0] == "start") { status.SessionId = parts[1]; status.Directory = Fixture("controller-capture", csv); status.State = "Recording"; File.AppendAllText(Path.Combine(bep, "LogOutput.log"), "[Error] fixture during capture\n"); }
                if (parts[0] == "stop") status.State = "Idle";
                await writer.WriteLineAsync(JsonSerializer.Serialize(status));
            }
        }
        catch (OperationCanceledException) { }
    });
    await using (var controller = new CaptureController())
    {
        await controller.ConnectAsync(pid); await controller.StartAsync("Monitor", 600, "fixture"); Check(controller.Active, "Start with 600-second limit");
        try { await controller.StartAsync("Monitor", 5, "duplicate"); throw new Exception("Duplicate accepted"); } catch (InvalidOperationException) { }
        Check(controller.Active, "Duplicate start stopped original recording");
        await controller.StopAsync(); Check(!controller.Active, "Stop");
        string log = File.ReadAllText(Path.Combine(controller.SessionDirectory!, "game-log.log")); Check(log.Contains("during capture", StringComparison.Ordinal) && !log.Contains("before capture", StringComparison.Ordinal), "Log boundaries");
    }
    cancellation.Cancel(); await server;
});
Test("Installed discovery finds Steam and profile candidates", () =>
{
    string? explicitGame = Environment.GetEnvironmentVariable("VPC_TEST_GAME_ROOT");
    if (explicitGame is not null) Check(Discovery.IsGame(explicitGame), "Explicit test game candidate is incomplete");
    DiscoveryResult found = Discovery.Find(explicitGame is null ? [] : [explicitGame]);
    if (explicitGame is not null) Check(found.Targets.Any(t => string.Equals(Path.GetFullPath(t.GameDirectory), Path.GetFullPath(explicitGame), StringComparison.OrdinalIgnoreCase)), "Explicit candidate not discovered");
    File.WriteAllText(Path.Combine(output, "discovery.json"), JsonSerializer.Serialize(found, new JsonSerializerOptions { WriteIndented = true }));
    Check(found.Targets.Any(t => Discovery.IsGame(t.GameDirectory)), "No installation discovered on this host");
    Check(found.Targets.Where(t => t.ProcessId == null && t.ProfileDirectory != null).All(t => !t.Evidence.StartsWith("Active", StringComparison.Ordinal)), "False active profile");
});
Test("Automatic setup is portable, idempotent and backs up an older recorder", () =>
{
    string root = Path.Combine(output, "Setup [test] O'Brien 测试");
    string bundle = Path.Combine(root, "relocated-app"), profile = Path.Combine(root, "profile");
    Directory.CreateDirectory(Path.Combine(bundle, "Recorder")); Directory.CreateDirectory(Path.Combine(profile, "BepInEx", "core"));
    File.WriteAllText(Path.Combine(profile, "BepInEx", "core", "BepInEx.dll"), "fixture");
    string source = Path.Combine(bundle, "Recorder", RecorderSetup.FileName); File.WriteAllText(source, "new-recorder");
    var target = new GameTarget(root, profile, "fixture");
    Check(RecorderSetup.Ensure(target, bundle, () => false).Ready, "Automatic installation");
    string installed = Path.Combine(target.BepInExRoot, "plugins", "ValheimPerformanceCompanion", RecorderSetup.FileName);
    Check(File.ReadAllText(installed) == "new-recorder", "Copied bundle-relative source");
    Check(RecorderSetup.Ensure(target, bundle, () => true).Ready, "Matching installed version needs no writes during gameplay");
    File.WriteAllText(source, "updated-recorder");
    Check(RecorderSetup.Ensure(target, bundle, () => true).WaitingForExit && File.ReadAllText(installed) == "new-recorder", "No update while game runs");
    Check(RecorderSetup.Ensure(target, bundle, () => false).Ready && File.ReadAllText(installed) == "updated-recorder", "Update after exit");
    Check(File.ReadAllText(Directory.GetFiles(Path.Combine(target.BepInExRoot, "PerformanceCompanion", "RecorderBackups")).Single()) == "new-recorder", "Restorable backup");
});
Test("Missing recorder waits for game exit and install race preserves the target", () =>
{
    string root = Path.Combine(output, "setup-race"); string bundle = Path.Combine(root, "bundle"), profile = Path.Combine(root, "profile");
    Directory.CreateDirectory(Path.Combine(bundle, "Recorder")); Directory.CreateDirectory(Path.Combine(profile, "BepInEx", "core"));
    File.WriteAllText(Path.Combine(bundle, "Recorder", RecorderSetup.FileName), "fixture");
    File.WriteAllText(Path.Combine(profile, "BepInEx", "core", "BepInEx.dll"), "fixture");
    var target = new GameTarget(root, profile, "fixture"); string installed = Path.Combine(target.BepInExRoot, "plugins", "ValheimPerformanceCompanion", RecorderSetup.FileName);
    Check(RecorderSetup.Ensure(target, bundle, () => true).WaitingForExit && !File.Exists(installed), "Missing recorder not installed into running game");
    int checks = 0; Check(RecorderSetup.Ensure(target, bundle, () => ++checks > 1).WaitingForExit && !File.Exists(installed), "Game started during staging");
    Check(RecorderSetup.Ensure(target, bundle, () => false).Ready, "Retry installs without deleting staged evidence");
});
Test("Refresh preserves the exact profile and finds its new PID", () =>
{
    var chosen = new GameTarget(@"C:\Game", @"C:\Profiles\chosen", "chosen");
    var other = new GameTarget(@"C:\Game", @"C:\Profiles\other", "running", 99);
    var running = chosen with { ProcessId = 101 };
    Check(TargetSelection.Choose([other, chosen, running], chosen)?.ProcessId == 101, "Exact match");
    Check(TargetSelection.Choose([other], chosen)?.ProcessId is null, "Other profile never borrowed");
    Check(TargetSelection.Choose([other], chosen, followDetected: true)?.ProfileDirectory == other.ProfileDirectory, "Automatic startup follows actual running profile rather than stale saved preference");
    Check(TargetSelection.Choose([chosen, other with { ProcessId = null }], null) is null, "No arbitrary profile install");
    Check(TargetSelection.RefreshInterval == TimeSpan.FromSeconds(30), "30 second interval");
});
if (args.Length > 1)
{
    Test("Real legacy capture import and report", () =>
    {
        var watch = Stopwatch.StartNew(); var c = CaptureReader.Read(args[1]); var a = Analyzer.Analyze(c, new());
        Check(a.Metrics.Frames > 1000 && a.Metrics.AverageFps > 1 && a.Metrics.AverageFps < 1000, "Real sample bounds");
        Check(c.Methods.Count > 0, "Method import"); Check(c.Frames.Any(f => f.GpuMs > 0), "Previous-frame GPU counters");
        File.WriteAllText(Path.Combine(output, "real-capture-report.html"), ReportExporter.Html(a));
        bool includeRaw = args.Length > 2 && args[2] == "--full";
        string reportPath = Path.Combine(output, includeRaw ? "real-capture-full-diagnostics.zip" : "real-capture-diagnostics.zip");
        ReportExporter.Export(a, reportPath, includeRaw);
        if (includeRaw)
        {
            using var archive = ZipFile.OpenRead(reportPath);
            var entry = archive.GetEntry("raw/cpu.etl.zip") ?? throw new Exception("Real diagnostics ZIP is missing the CPU trace.");
            using var sourceStream = File.OpenRead(Path.Combine(c.Directory, "cpu.etl.zip"));
            using var archiveStream = entry.Open();
            Check(SHA256.HashData(sourceStream).SequenceEqual(SHA256.HashData(archiveStream)), "Real CPU trace preserved byte for byte");
        }
        File.WriteAllText(Path.Combine(output, "real-capture-metrics.json"), JsonSerializer.Serialize(new { a.Metrics, Findings = a.Findings, ImportAndAnalyzeMs = watch.ElapsedMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
        System.Console.WriteLine($"REAL_CAPTURE frames={a.Metrics.Frames} fps={a.Metrics.AverageFps:F3} p99={a.Metrics.P99Ms:F3} import_ms={watch.ElapsedMilliseconds}");
    });
}
System.Console.WriteLine($"RESULT {passed} passed; {failed} failed");
Environment.ExitCode = failed == 0 ? 0 : 1;
