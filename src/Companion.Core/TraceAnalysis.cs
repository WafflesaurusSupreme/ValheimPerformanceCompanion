using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Companion.Core;

public sealed record CpuObservation(double Seconds, int ThreadId, string Leaf, string Stack);
public sealed record TraceSummary(int ProcessId, int Samples, int MissingStacks, long EventsLost, DateTime TraceStartUtc, DateTime TraceEndUtc);

public static class TraceAnalysis
{
    public static TraceSummary Generate(string directory, int processId, CancellationToken cancellation = default)
    {
        string root = Path.GetFullPath(directory);
        string metadata = Path.Combine(root, "recorder.json");
        using var json = JsonDocument.Parse(File.ReadAllText(metadata));
        DateTimeOffset recorderStart = DateTimeOffset.Parse(json.RootElement.GetProperty("StartUtc").GetString()!, CultureInfo.InvariantCulture);
        int mainThread = json.RootElement.TryGetProperty("MainThreadId", out var mainProperty) && mainProperty.TryGetInt32(out int mainValue) ? mainValue : 0;
        string cache = Path.Combine(root, "analysis-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        string etl = Path.Combine(root, "cpu.etl");
        if (!File.Exists(etl))
        {
            using var archive = ZipFile.OpenRead(Path.Combine(root, "cpu.etl.zip"));
            ZipArchiveEntry entry = archive.Entries.Single(e => Path.GetFileName(e.FullName).Equals("cpu.etl", StringComparison.OrdinalIgnoreCase));
            if (entry.Length > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("ETL exceeds the 4 GB analysis limit.");
            etl = Path.Combine(cache, "cpu.etl");
            using var source = entry.Open(); using var destination = new FileStream(etl, FileMode.CreateNew, FileAccess.Write);
            byte[] buffer = new byte[65536]; long total = 0; int read;
            while ((read = source.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); total += read; if (total > entry.Length) throw new InvalidDataException("Trace decompression exceeded declared length."); destination.Write(buffer, 0, read); }
        }
        if (new FileInfo(etl).Length > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("ETL exceeds the analysis size limit.");
        using var log = new StreamWriter(Path.Combine(cache, "conversion.log"), false, new UTF8Encoding(false));
        string etlx = TraceLog.CreateFromEventTraceLogFile(etl, Path.Combine(cache, "cpu.etlx"), new TraceLogOptions { ConversionLog = log, LocalSymbolsOnly = true });
        using var trace = new TraceLog(etlx);
        string temporary = Path.Combine(cache, "cpu-samples.csv");
        int count = 0, missing = 0;
        var pending = new Dictionary<int, (double Seconds, string Reason, string State)>();
        int waitCount = 0, ioCount = 0, omittedWaits = 0; double firstSample = double.PositiveInfinity, lastSample = double.NegativeInfinity;
        using var waits = new StreamWriter(Path.Combine(cache, "thread-waits.csv"), false, new UTF8Encoding(false));
        using var disk = new StreamWriter(Path.Combine(cache, "disk-io.csv"), false, new UTF8Encoding(false));
        waits.WriteLine("Start,End,ThreadId,Reason,State"); disk.WriteLine("Seconds,ThreadId,DurationMs,Bytes,Operation");
        using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
        {
            writer.WriteLine("Seconds,ThreadId,Leaf,Stack");
            foreach (var entry in trace.Events)
            {
                cancellation.ThrowIfCancellationRequested();
                double eventSeconds = (entry.TimeStamp.ToUniversalTime() - recorderStart.UtcDateTime).TotalSeconds;
                if (entry is CSwitchTraceData change)
                {
                    if (change.NewProcessID == processId && pending.Remove(change.NewThreadID, out var off) && eventSeconds >= off.Seconds)
                    {
                        if (waitCount >= 1_000_000) omittedWaits++;
                        else { waitCount++; waits.WriteLine(FormattableString.Invariant($"{off.Seconds:F9},{eventSeconds:F9},{change.NewThreadID},{Csv.Escape(off.Reason)},{Csv.Escape(off.State)}")); }
                    }
                    if (change.OldProcessID == processId && (mainThread == 0 || change.OldThreadID == mainThread)) pending[change.OldThreadID] = (eventSeconds, change.OldThreadWaitReason.ToString(), change.OldThreadState.ToString());
                }
                if (entry is DiskIOTraceData io && io.ProcessID == processId)
                {
                    if (++ioCount > 1_000_000) throw new InvalidDataException("Disk event count exceeds one million");
                    disk.WriteLine(FormattableString.Invariant($"{eventSeconds:F9},{io.ThreadID},{io.ElapsedTimeMSec:F6},{io.TransferSize},{Csv.Escape(io.EventName)}"));
                }
                if (entry is not SampledProfileTraceData sample || sample.ProcessID != processId) continue;
                if (++count > 1_000_000) throw new InvalidDataException("CPU sample count exceeds the one-million-sample analysis limit.");
                if (writer.BaseStream.Position > 512L * 1024 * 1024) throw new InvalidDataException("CPU sample output exceeds 512 MB; partial analysis retained in its cache.");
                var names = new List<string>(32);
                for (TraceCallStack? stack = sample.CallStack(); stack != null && names.Count < 128; stack = stack.Caller)
                {
                    var address = stack.CodeAddress;
                    string method = address.FullMethodName;
                    string module = address.ModuleFile?.Name ?? "unknown";
                    names.Add(Clean(module + "!" + (string.IsNullOrEmpty(method) ? "0x" + address.Address.ToString("x", CultureInfo.InvariantCulture) : method)));
                }
                if (names.Count == 0) { missing++; names.Add("Unresolved stack"); }
                double seconds = (sample.TimeStamp.ToUniversalTime() - recorderStart.UtcDateTime).TotalSeconds;
                firstSample = Math.Min(firstSample, seconds); lastSample = Math.Max(lastSample, seconds);
                writer.WriteLine(seconds.ToString("F6", CultureInfo.InvariantCulture) + "," + sample.ThreadID.ToString(CultureInfo.InvariantCulture) + "," + Csv.Escape(names[0]) + "," + Csv.Escape(string.Join(" ← ", names)));
            }
        }
        if (count == 0) throw new InvalidDataException("No CPU samples match the selected process ID. Raw trace retained; no attribution made.");
        var summary = new TraceSummary(processId, count, missing, trace.EventsLost, trace.SessionStartTime.ToUniversalTime(), trace.SessionEndTime.ToUniversalTime());
        File.Move(temporary, Path.Combine(root, "cpu-samples.csv"));
        waits.Dispose(); disk.Dispose();
        File.Move(Path.Combine(cache, "thread-waits.csv"), Path.Combine(root, "thread-waits.csv"));
        File.Move(Path.Combine(cache, "disk-io.csv"), Path.Combine(root, "disk-io.csv"));
        File.WriteAllText(Path.Combine(root, "trace-coverage.json"), JsonSerializer.Serialize(new { FirstCpuSampleSeconds = firstSample, LastCpuSampleSeconds = lastSample, CpuSamples = count, CompleteWaitIntervals = waitCount, OmittedWaitIntervals = omittedWaits, WaitThreadScope = mainThread > 0 ? "Recorded main thread only" : "All process threads; main identity unavailable", UnfinishedWaitIntervals = pending.Count, DiskEvents = ioCount, trace.EventsLost, Note = "Observed sample extent is not continuous coverage. Missing switch events, waits at trace boundaries and unresolved thread identity limit attribution." }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "cpu-analysis.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return summary;
    }
    private static string Clean(string value) => string.Concat(value.Take(1024).Select(c => char.IsControl(c) ? ' ' : c));
}
