using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Companion.Core;

public static partial class CaptureReader
{
    public static Capture Read(string input, CancellationToken cancellation = default)
    {
        string path = Path.GetFullPath(input);
        string directory = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!System.IO.Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        string[] choices = System.IO.Directory.GetFiles(directory, "frames*.csv");
        string framePath = File.Exists(path) ? path : choices.Length == 1 ? choices[0]
            : throw new InvalidDataException(choices.Length == 0 ? "No frame recording found. Select a capture folder or frames CSV." : "This folder has multiple recordings. Select the specific frames CSV.");
        var capture = new Capture { Directory = directory, Name = new DirectoryInfo(directory).Name };
        int rowNumber = 0, skipped = 0;
        double previous = -1;
        bool recoverTail = File.Exists(Path.Combine(directory, "recorder.json")) && !File.Exists(Path.Combine(directory, "completed.json"));
        using var records = Csv.Read(framePath).GetEnumerator();
        while (true)
        {
            try { if (!records.MoveNext()) break; }
            catch (InvalidDataException ex) when (recoverTail && capture.Frames.Count >= 2)
            { capture.Warnings.Add("Partial recording: retained the valid prefix of the frame file. " + ex.Message); break; }
            var row = records.Current;
            cancellation.ThrowIfCancellationRequested();
            int index = rowNumber++;
            double? interval = row.Number("FrameIntervalMs"), elapsed = row.Number("ElapsedMs");
            if (interval is null || elapsed is null || interval < 0 || elapsed < 0 || elapsed <= previous
                || !DateTimeOffset.TryParse(row.Text("UtcTimestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
            { skipped++; continue; }
            previous = elapsed.Value;
            if (capture.Frames.Count == 0) capture.StartUtc = utc - TimeSpan.FromMilliseconds(elapsed.Value);
            capture.Frames.Add(new Frame(elapsed.Value / 1000, interval.Value, utc) { Index = index,
                Focused = Flag(row, "Focused"), HasPlayer = Flag(row, "HasPlayer"), Gc0 = (int?)row.Number("Gc0"),
                CpuMs = Positive(row.Number("CpuMainMs")), GpuMs = Positive(row.Number("GpuFrameMs")), Context = row.Text("Biome") });
        }
        if (capture.Frames.Count < 2) throw new InvalidDataException("The recording needs at least two valid frame timestamps.");
        if (skipped > 0) capture.Warnings.Add($"Skipped {skipped} invalid or out-of-order frame rows.");
        string suffix = Path.GetFileName(framePath)["frames".Length..];
        ReadOptional(capture, "details" + suffix, p => ReadDetails(capture, p, cancellation));
        ReadOptional(capture, "methods" + suffix, p => ReadMethods(capture, p, cancellation));
        ReadOptional(capture, "session.json", p => ReadMetadata(capture, p));
        ReadOptional(capture, "recorder.json", p => ReadMetadata(capture, p));
        ReadOptional(capture, "configuration.json", p => ReadMetadata(capture, p));
        ReadOptional(capture, "configuration-warning.txt", p => capture.Warnings.Add(File.ReadAllText(p)));
        ReadOptional(capture, "deep-quality.txt", p => capture.Warnings.Add(File.ReadAllText(p)));
        ReadOptional(capture, "completed.json", p => ReadMetadata(capture, p));
        ReadOptional(capture, "cpu-analysis.json", p => ReadMetadata(capture, p));
        ReadOptional(capture, "trace-coverage.json", p => ReadMetadata(capture, p));
        ReadOptional(capture, "cpu-samples.csv", p =>
        {
            foreach (var row in Csv.Read(p, 1000000))
                if (row.Number("Seconds") is double seconds && row.Number("ThreadId") is double thread)
                    capture.CpuSamples.Add(new(seconds, (int)thread, row.Text("Leaf"), row.Text("Stack")));
        });
        if (capture.Metadata.TryGetValue("EventsLost", out string? loss) && loss != "0") capture.Warnings.Add("ETW reported " + loss + " lost events; CPU evidence is incomplete.");
        ReadOptional(capture, "capture-health.txt", p => capture.Warnings.AddRange(File.ReadAllLines(p).Where(s => s.Length > 0)));
        foreach (string logName in new[] { "perfview-stop.log", "perfview-export.log" })
            ReadOptional(capture, logName, p =>
            {
                if (new FileInfo(p).Length > 8 * 1024 * 1024) return;
                if (File.ReadLines(p).Any(line => line.Contains("EVENTS LOST", StringComparison.OrdinalIgnoreCase) || line.Contains("events were lost", StringComparison.OrdinalIgnoreCase)))
                    capture.Warnings.Add("PerfView reported event loss. CPU evidence may be incomplete; inspect " + logName + ".");
            });
        ReadOptional(capture, "markers.csv", p =>
        {
            foreach (var row in Csv.Read(p, 10000))
                capture.Events.Add(new(row.Number("ElapsedMs") / 1000, "Marker", row.Text("Label"), 0));
        });
        string? log = new[] { "game-log.log", "game-log-at-stop.log", "LogOutput.log" }.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
        if (log is not null) ReadOptional(capture, Path.GetFileName(log), p => ReadLog(capture, p, cancellation));
        else capture.Warnings.Add("No game log accompanies this recording.");
        if (capture.Metadata.ContainsKey("Protocol") && !File.Exists(Path.Combine(directory, "completed.json")))
            capture.Warnings.Add("Recording has no completion receipt. It may be partial; the last buffered samples may be missing.");
        if (capture.Metadata.TryGetValue("DroppedFrames", out string? dropped) && dropped != "0") capture.Warnings.Add($"Recorder dropped {dropped} samples. Rates span only retained intervals.");
        if (capture.Frames.All(f => f.GpuMs is null)) capture.Warnings.Add("GPU frame timing is unavailable; GPU bottlenecks cannot be established.");
        if (capture.Frames.All(f => f.Focused is null)) capture.Warnings.Add("Focus/player state is unavailable; automatic exclusions cannot be applied.");
        capture.Warnings.Add("Game-loop intervals are not display-presented frames. Method timings are inclusive and must not be summed.");
        DiagnosticReader.Read(capture, cancellation);
        return capture;
    }
    private static void ReadOptional(Capture capture, string file, Action<string> read)
    {
        string path = Path.Combine(capture.Directory, file);
        if (!File.Exists(path)) return;
        try { read(path); }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException or InvalidDataException)
        { capture.Warnings.Add($"{file}: {ex.Message}"); }
    }
    private static bool? Flag(Dictionary<string, string> row, string name) => row.Text(name) switch { "1" => true, "0" => false, _ => null };
    private static double? Positive(double? value) => value > 0 ? value : null;
    private static void ReadDetails(Capture capture, string path, CancellationToken cancellation)
    {
        var frames = capture.Frames.ToDictionary(f => f.Index);
        foreach (var row in Csv.Read(path))
        {
            cancellation.ThrowIfCancellationRequested();
            if (row.Number("FrameIndex") is not double index || !frames.TryGetValue((int)index, out Frame? frame)) continue;
            frame.Focused = Flag(row, "Focused"); frame.HasPlayer = Flag(row, "HasPlayer"); frame.Gc0 = (int?)row.Number("Gc0");
            frame.Context = $"Biome {row.Text("Biome")} · {row.Text("LoadedCharacters")} characters · {row.Text("Environment")}";
            // Keep these as contextual observations; Unity timing stamps can lag the observed game-loop interval.
            if (row.Text("HasFrameTiming") == "1") { frame.CpuMs = Positive(row.Number("CpuMainMs")); frame.GpuMs = Positive(row.Number("GpuFrameMs")); }
            frame.CpuMs ??= Positive(row.Number("Main Thread [TimeNanoseconds] previous completed frame") / 1e6);
            frame.GpuMs ??= Positive(row.Number("GPU Frame Time [TimeNanoseconds] previous completed frame") / 1e6);
        }
    }
    private static void ReadMethods(Capture capture, string path, CancellationToken cancellation)
    {
        var previous = new Dictionary<string, (double Time, double Ms, long Calls)>();
        foreach (var row in Csv.Read(path))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!DateTimeOffset.TryParse(row.Text("UtcTimestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)) continue;
            string name = row.Text("Probe");
            double end = (utc - capture.StartUtc).TotalSeconds, ms = row.Number("CumulativeMainMs") ?? 0;
            long calls = (long)(row.Number("CumulativeCompletedCalls") ?? 0);
            var old = previous.GetValueOrDefault(name);
            if (end > old.Time && ms >= old.Ms && calls >= old.Calls)
                capture.Methods.Add(new(name, old.Time, end, ms - old.Ms, calls - old.Calls, (int)(row.Number("SampleEvery") ?? 1)));
            previous[name] = (end, ms, calls);
        }
    }
    private static void ReadMetadata(Capture capture, string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Metadata exceeds 4 MB.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Metadata must contain a JSON object; valid frames have been retained.");
        foreach (var property in json.RootElement.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                capture.Metadata[property.Name] = property.Value.ToString();
    }
    private static void ReadLog(Capture capture, string path, CancellationToken cancellation)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) { capture.Warnings.Add("Game log exceeds 64 MB; log interpretation skipped."); return; }
        int line = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is string text)
        {
            cancellation.ThrowIfCancellationRequested(); line++;
            string category = text.Contains("Exception", StringComparison.OrdinalIgnoreCase) || text.Contains("[Error", StringComparison.OrdinalIgnoreCase) ? "Error"
                : text.Contains("Dungeon", StringComparison.OrdinalIgnoreCase) && (text.Contains("generat", StringComparison.OrdinalIgnoreCase) || text.Contains("rooms", StringComparison.OrdinalIgnoreCase)) ? "Generation"
                : text.Contains("saved", StringComparison.OrdinalIgnoreCase) || text.Contains("saving", StringComparison.OrdinalIgnoreCase) ? "Save"
                : text.Contains("Spawn", StringComparison.OrdinalIgnoreCase) ? "Spawning" : "";
            if (category.Length == 0) continue;
            double? seconds = null;
            Match stamp = IsoStamp().Match(text);
            if (stamp.Success && DateTimeOffset.TryParse(stamp.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
                seconds = (utc - capture.StartUtc).TotalSeconds;
            // Local-time-only log lines cannot be safely aligned across timezone and midnight boundaries.
            capture.Events.Add(new(seconds, category, text.Length > 1600 ? text[..1600] : text, line));
            if (capture.Events.Count >= 25000) { capture.Warnings.Add("Log event limit reached; additional events omitted."); break; }
        }
    }
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex IsoStamp();
}
