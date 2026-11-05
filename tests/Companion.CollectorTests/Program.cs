extern alias service;
using System.Buffers.Binary;
using System.Text;
using System.IO.Pipes;
using Companion.Collector;
using ServiceSecurity = service::Companion.Collector.ServiceSecurity;
using ServiceCaller = service::Companion.Collector.Caller;
using Companion.Core;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
void Reject(Action action, string name)
{
    try { action(); throw new Exception("Accepted " + name); }
    catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException) { passed++; }
}
var valid = new ServiceRequest(1, "start", Guid.NewGuid().ToString("N"), 123, 456, 5, "Standard");
foreach (string command in new[] { "start", "begin", "stop", "status", "release" })
    CollectorProtocol.Validate(valid with { Command = command });
foreach (ServiceRequest bad in new[]
{
    valid with { Protocol = 2 }, valid with { Command = "execute" }, valid with { SessionId = "../capture" },
    valid with { SessionId = "" }, valid with { OwnerPid = 0 }, valid with { GamePid = -1 },
    valid with { Seconds = 4 }, valid with { Seconds = 601 }, valid with { Mode = "Monitor" }, valid with { Mode = "Deep\n/run" }
}) Reject(() => CollectorProtocol.Validate(bad), "invalid collector request");
foreach (string sid in new[] { "..", "S-1-5-21/../x", "S-1-5:21", "" })
    Reject(() => CollectorProtocol.SessionDirectory(sid, valid.SessionId), "path-like SID");
Reject(() => CollectorProtocol.SessionDirectory("S-1-5-21-123", "../escape"), "path-like session");
Check(CollectorProtocol.SessionDirectory("S-1-5-21-123", valid.SessionId).EndsWith(Path.Combine("S-1-5-21-123", valid.SessionId), StringComparison.Ordinal), "Session containment");
string canonical = Path.Combine(Path.GetTempPath(), "VpcBinding", valid.SessionId);
var binding = new CollectorRequest(valid.SessionId, canonical, valid.GamePid, valid.OwnerPid, valid.Seconds, valid.Mode);
CollectorStaging.ValidateBinding(binding, binding, canonical);
foreach (CollectorRequest changed in new[] { binding with { Directory = Path.GetPathRoot(canonical)! }, binding with { SessionId = Guid.NewGuid().ToString("N") },
    binding with { OwnerPid = 99 }, binding with { GamePid = 99 }, binding with { Seconds = 600 }, binding with { Mode = "Deep" } })
    Reject(() => CollectorStaging.ValidateBinding(changed, binding, canonical), "retained collector metadata binding");
await using (var stream = new MemoryStream())
{
    await CollectorProtocol.WriteAsync(stream, valid, CancellationToken.None);
    stream.Position = 0;
    Check(await CollectorProtocol.ReadAsync<ServiceRequest>(stream, CancellationToken.None) == valid, "Protocol round trip");
}
foreach (int length in new[] { -1, 0, CollectorProtocol.MaximumMessageBytes + 1, int.MaxValue })
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
    await using var stream = new MemoryStream(header);
    try { await CollectorProtocol.ReadAsync<ServiceRequest>(stream, CancellationToken.None); throw new Exception("Unbounded message accepted"); }
    catch (InvalidDataException) { passed++; }
}
foreach (string json in new[] { "null", "{\"Protocol\":1,\"OutputPath\":\"C:\\\\Windows\"}", "{broken" })
{
    byte[] body = Encoding.UTF8.GetBytes(json), frame = new byte[body.Length + 4];
    BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length); body.CopyTo(frame, 4);
    await using var stream = new MemoryStream(frame);
    try { await CollectorProtocol.ReadAsync<ServiceRequest>(stream, CancellationToken.None); throw new Exception("Malformed/unknown protocol data accepted"); }
    catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { passed++; }
}
await using (var stream = new MemoryStream(new byte[] { 1, 2 }))
{
    try { await CollectorProtocol.ReadAsync<ServiceRequest>(stream, CancellationToken.None); throw new Exception("Truncated frame accepted"); }
    catch (EndOfStreamException) { passed++; }
}
Reject(() => ServiceSecurity.ValidateInstallation(Path.GetTempPath()), "user-writable service location");
using (var current = System.Diagnostics.Process.GetCurrentProcess())
{
    long started = current.StartTime.ToUniversalTime().Ticks;
    Check(ServiceSecurity.OwnerIsAlive(new ServiceCaller(current.Id,started,"S-1-5-21-123")), "Live owner was incorrectly classified as abandoned");
    Check(!ServiceSecurity.OwnerIsAlive(new ServiceCaller(current.Id,started-1,"S-1-5-21-123")), "A reused owner PID was treated as the original process");
}
string recoveryRoot = Path.Combine(Path.GetTempPath(), "VpcMissingEvidence-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(recoveryRoot);
File.WriteAllText(Path.Combine(recoveryRoot, "frames.csv"), "UtcTimestamp,ElapsedMs,FrameIntervalMs\n2026-10-29T12:00:00Z,0,0\n2026-10-29T12:00:00.016Z,16,16\n");
foreach (string mode in new[] { "Monitor", "Standard", "Deep" })
{
    File.WriteAllText(Path.Combine(recoveryRoot, "recorder.json"), System.Text.Json.JsonSerializer.Serialize(new { Protocol = 1, Mode = mode }));
    var recording = CaptureReader.Read(recoveryRoot);
    Check(recording.Warnings.Any(w => w.StartsWith("ETW CPU evidence is missing", StringComparison.Ordinal)) == (mode != "Monitor"), "Missing ETW evidence was hidden or Monitor was misclassified");
    Check(recording.Warnings.Any(w => w.StartsWith("Deep ETW disk/context-switch", StringComparison.Ordinal)) == (mode == "Deep"), "Missing Deep evidence was hidden or another mode was misclassified");
}
string publicationRoot = Path.Combine(Path.GetTempPath(), "VpcAnalysisPublication-" + Guid.NewGuid().ToString("N"));
DateTimeOffset anchor = DateTimeOffset.Parse("2026-10-29T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
var expectedSummary = new TraceSummary(123, 1, 0, 0, anchor.UtcDateTime, anchor.AddSeconds(1).UtcDateTime);
string[] analysisNames = ["cpu-samples.csv", "thread-waits.csv", "disk-io.csv", "trace-coverage.json", "cpu-analysis.json"];
(string Root, string Generation) MakeGeneration(string name)
{
    string root = Path.Combine(publicationRoot, name), generation = Path.Combine(root, "analysis-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(generation);
    File.WriteAllBytes(Path.Combine(root, "cpu.etl"), [1, 2, 3]);
    File.WriteAllText(Path.Combine(root, "recorder.json"), System.Text.Json.JsonSerializer.Serialize(new { StartUtc = anchor }));
    File.WriteAllText(Path.Combine(generation, "cpu-samples.csv"), "Seconds,ThreadId,Leaf,Stack\n0,1,Test,Test\n");
    File.WriteAllText(Path.Combine(generation, "thread-waits.csv"), "Start,End,ThreadId,Reason,State\n");
    File.WriteAllText(Path.Combine(generation, "disk-io.csv"), "Seconds,ThreadId,DurationMs,Bytes,Operation\n");
    File.WriteAllText(Path.Combine(generation, "trace-coverage.json"), "{}");
    File.WriteAllText(Path.Combine(generation, "cpu-analysis.json"), System.Text.Json.JsonSerializer.Serialize(expectedSummary));
    return (root,generation);
}
var partial = MakeGeneration("legacy-partial");
File.Copy(Path.Combine(partial.Generation,"cpu-samples.csv"),Path.Combine(partial.Root,"cpu-samples.csv"));
Check(!AnalysisPublication.IsComplete(partial.Root,123), "A CPU CSV alone was treated as complete analysis");
AnalysisPublication.Commit(partial.Root,partial.Generation,123,anchor,Path.Combine(partial.Root,"cpu.etl"));
Check(AnalysisPublication.IsComplete(partial.Root,123) && analysisNames.All(name => File.Exists(Path.Combine(partial.Root,name))), "Partial legacy analysis did not finish publication");
AnalysisTransaction originalTransaction = CollectorStaging.ReadReceipt<AnalysisTransaction>(Path.Combine(partial.Root,"analysis-pending.json"));
var interrupted = MakeGeneration("interrupted-publication");
File.Copy(Path.Combine(interrupted.Generation,"cpu-samples.csv"),Path.Combine(interrupted.Root,"cpu-samples.csv"));
var pendingTransaction = originalTransaction with { Generation = Path.GetFileName(interrupted.Generation) };
File.WriteAllText(Path.Combine(interrupted.Root,"analysis-pending.json"),System.Text.Json.JsonSerializer.Serialize(pendingTransaction));
Check(AnalysisPublication.TryResume(interrupted.Root,123,out TraceSummary? resumed) && resumed == expectedSummary && AnalysisPublication.IsComplete(interrupted.Root,123), "Interrupted publication did not resume its frozen generation");
var conflict = MakeGeneration("unrelated-output");
File.WriteAllText(Path.Combine(conflict.Root,"disk-io.csv"),"unrelated user output");
File.WriteAllText(Path.Combine(conflict.Root,"analysis-pending.json"),System.Text.Json.JsonSerializer.Serialize(originalTransaction with { Generation = Path.GetFileName(conflict.Generation) }));
try { AnalysisPublication.TryResume(conflict.Root,123,out _); throw new Exception("Unrelated output was overwritten"); }
catch (IOException) { Check(File.ReadAllText(Path.Combine(conflict.Root,"disk-io.csv")) == "unrelated user output", "Conflicting evidence was not preserved"); }
Check(!AnalysisPublication.IsComplete(interrupted.Root,999), "Analysis for another PID was accepted");
using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    string name = "VpcCollectorReconnect-" + Guid.NewGuid().ToString("N");
    await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
    using (var broken = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        Task connected = server.WaitForConnectionAsync(cancellation.Token);
        await broken.ConnectAsync(2000, cancellation.Token); await connected;
    }
    try { await CollectorProtocol.ReadAsync<ServiceRequest>(server, cancellation.Token); throw new Exception("Disconnected client was accepted"); }
    catch (EndOfStreamException) { passed++; }
    ServiceSecurity.Disconnect(server);
    using var replacement = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    Task reconnected = server.WaitForConnectionAsync(cancellation.Token);
    await replacement.ConnectAsync(2000, cancellation.Token); await reconnected;
    Task<ServiceRequest> nextRequest = CollectorProtocol.ReadAsync<ServiceRequest>(server, cancellation.Token);
    await CollectorProtocol.WriteAsync(replacement, valid, cancellation.Token);
    Check(await nextRequest == valid, "Service pipe did not recover after an early client disconnect");
    var expectedReply = new ServiceReply(true, "Fully consumed reply", "Ready");
    Task replyWritten = CollectorProtocol.WriteReplyAsync(server, expectedReply, cancellation.Token);
    await Task.Delay(50, cancellation.Token);
    Check(!replyWritten.IsCompleted, "Service recycled a reply before client consumption");
    Check(await CollectorProtocol.ReadReplyAsync(replacement, cancellation.Token) == expectedReply, "Fast service reply was truncated");
    await replyWritten;
    ServiceSecurity.Disconnect(server);
}
Console.WriteLine($"RESULT {passed} collector protocol/security checks passed");
