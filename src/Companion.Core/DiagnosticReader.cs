using System.Security.Cryptography;

namespace Companion.Core;

internal static class DiagnosticReader
{
    internal static void Read(Capture capture, CancellationToken cancellation)
    {
        void ReadFile(string name, Action<string> read)
        {
            string path = Path.Combine(capture.Directory, name);
            if (!File.Exists(path)) return;
            try { if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("Diagnostic file exceeds 128 MB"); read(path); }
            catch (Exception error) when (error is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
            { capture.Warnings.Add(name + ": " + error.Message); }
        }
        foreach (string name in new[] { "plugins.txt", "gc-capability.txt", "gc-quality.txt", "code-quality.txt", "collector-quality.txt" }.Concat(Directory.GetFiles(capture.Directory, "coverage-*.txt").Select(p => Path.GetFileName(p))))
            ReadFile(name, p => { if (new FileInfo(p).Length > 1024 * 1024) throw new InvalidDataException("Capability file exceeds 1 MB"); foreach (string line in File.ReadLines(p)) if (name == "plugins.txt") capture.Metadata["Plugin:" + line.Split('=')[0]] = line; else if (!line.StartsWith("METHOD ", StringComparison.Ordinal) && !line.StartsWith("LOOP ", StringComparison.Ordinal) && !line.StartsWith("COUNTER ", StringComparison.Ordinal)) capture.Warnings.Add(line); });
        ReadFile("modules.csv", p => { foreach (var r in Csv.Read(p, 4096)) capture.Modules.Add(new(r.Text("Assembly"), r.Text("Version"), r.Text("Mvid"), r.Text("FileSha256"), r.Text("Owners"), r.Text("LocationKind"))); });
        foreach (string path in Directory.GetFiles(capture.Directory, "patches-*.csv"))
            ReadFile(Path.GetFileName(path), p => { foreach (var r in Csv.Read(p, 100000)) capture.Patches.Add(new(r.Text("Target"), r.Text("Kind"), r.Text("Owner"), r.Text("Assembly"), r.Text("PatchMethod"), r.Text("TargetMvid"), r.Text("TargetToken"), r.Text("PatchMvid"), r.Text("PatchToken"))); });
        ReadFile("code-methods.csv", p =>
        {
            foreach (var r in Csv.Read(p, 150000))
            {
                cancellation.ThrowIfCancellationRequested();
                if (r.Text("IlBase64").Length > 200000 || r.Text("Instructions").Length > 2 * 1024 * 1024) throw new InvalidDataException("Method code exceeds limits");
                byte[] bytes = Convert.FromBase64String(r.Text("IlBase64"));
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(r.Text("IlSha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Code IL hash mismatch");
                capture.Code.Add(new(r.Text("Assembly"), r.Text("Mvid"), r.Text("Token"), r.Text("Type"), r.Text("Method"), r.Text("Signature"), r.Text("IlSha256"), r.Text("IlBase64"), r.Text("Instructions"), r.Text("Locals"), r.Text("ExceptionClauses")));
            }
        });
        ReadFile("gc-events.csv", p => { foreach (var r in Csv.Read(p, 65536)) if (r.Number("Seconds") is double s && r.Number("Phase") is double phase && r.Number("ThreadId") is double tid) capture.GcPhases.Add(new(s, (int)tid, (int)phase, (int)(r.Number("Generation") ?? 0))); });
        ReadFile("thread-waits.csv", p => { foreach (var r in Csv.Read(p, 1000000)) if (r.Number("Start") is double s && r.Number("End") is double e && e >= s) capture.Waits.Add(new(s, e, (int)(r.Number("ThreadId") ?? 0), r.Text("Reason"), r.Text("State"))); });
        ReadFile("disk-io.csv", p => { foreach (var r in Csv.Read(p, 1000000)) if (r.Number("Seconds") is double s) capture.Disk.Add(new(s, (int)(r.Number("ThreadId") ?? 0), r.Number("DurationMs") ?? 0, (long)(r.Number("Bytes") ?? 0), r.Text("Operation"))); });
        if (capture.CpuSamples.Count > 0)
        {
            double first = capture.CpuSamples.Min(s => s.Seconds), last = capture.CpuSamples.Max(s => s.Seconds);
            capture.Warnings.Add($"CPU samples cover {first:F3}–{last:F3}s. This is the observed sample extent, not proof of continuous coverage; zero lost events does not exclude circular-buffer overwrite.");
        }
        capture.Warnings.Add("RecorderSampleMs measures frame sampling only. Total method-probe, ETW, writer, and legacy-recorder overhead requires a matched comparison between capture modes.");
        if (capture.Metadata.TryGetValue("OmittedWaitIntervals", out string? omitted) && omitted != "0") capture.Warnings.Add("Thread wait export omitted " + omitted + " intervals at its capacity limit; coverage is incomplete.");
        if (capture.Metadata.TryGetValue("WaitThreadScope", out string? scope)) capture.Warnings.Add("Off-CPU observation scope: " + scope);
        if (string.Equals(capture.Metadata.GetValueOrDefault("LegacyRecorderDetected"), "True", StringComparison.OrdinalIgnoreCase) || capture.Patches.Any(p => p.Owner.Contains("riverheimcascades.diagnostics", StringComparison.OrdinalIgnoreCase)))
            capture.Warnings.Add("Legacy Riverheim instrumentation is present. Combined probe overhead can affect CPU cost and allocation frequency; perform a matched overhead comparison before attributing it to gameplay code.");
    }
}
