using System.Globalization;
using System.IO;
using System.Text.Json;
using Companion.Core;

namespace Companion.App;

public sealed record RecentCapture(string Path, DateTime ModifiedUtc)
{
    public DateTime StartedUtc { get; init; } = ModifiedUtc;
    public string Scenario { get; init; } = "";
    public string Mode { get; init; } = "";
    public string MetadataWarning { get; init; } = "";
    public string Timestamp => StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string RecordingName => $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))} / {System.IO.Path.GetFileName(Path)}";
    public string Label => $"{Timestamp} · {CaptureName(Path)}";
    public override string ToString() => Label;

    private static string CaptureName(string path)
    {
        string name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "Capture";
        return name.Length <= 24 ? name : name[..21] + "…";
    }
}

public sealed record SavedComparison(string BaselinePath, string OtherPath, DateTime SavedUtc, AnalysisOptions Options)
{
    public string Label => $"{SavedUtc.ToLocalTime():g} · {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(BaselinePath))} vs {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(OtherPath))}";
    public override string ToString() => Label;
}

public sealed class CaptureHistory
{
    private sealed record State(string? LastOpened, string[] Paths, SavedComparison[] Comparisons);
    private readonly string stateFile;

    public CaptureHistory(string stateFile) => this.stateFile = stateFile;

    public static string? CaptureRoot(string? baseDirectory) => baseDirectory is null ? null
        : System.IO.Path.Combine(baseDirectory, "BepInEx", "PerformanceCompanion", "Captures");

    public (IReadOnlyList<RecentCapture> Recent, string? LastOpened, IReadOnlyList<SavedComparison> Comparisons) Read(string? profileDirectory)
    {
        var snapshot = ReadLibrary(profileDirectory);
        return (snapshot.Recent, snapshot.LastOpened, snapshot.Comparisons);
    }

    public (IReadOnlyList<RecentCapture> Recent, string? LastOpened, IReadOnlyList<SavedComparison> Comparisons, IReadOnlyList<string> Warnings) ReadLibrary(string? baseDirectory)
    {
        var warnings = new List<string>();
        State state;
        try { state = ReadState(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add("Saved history could not be read: " + ex.Message);
            state = new(null, [], []);
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in state.Paths) Add(path);
        foreach (SavedComparison comparison in state.Comparisons)
        { Add(comparison.BaselinePath); Add(comparison.OtherPath); }
        string? root = CaptureRoot(baseDirectory);
        if (root is not null && Directory.Exists(root))
        {
            try
            {
                Scan(root);
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    Scan(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Some capture folders could not be listed: " + ex.Message); }
        }
        string? last = state.LastOpened is not null && paths.Contains(state.LastOpened) ? state.LastOpened : null;
        var recent = paths.Select(ReadSummary).OrderByDescending(item => item.StartedUtc)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        SavedComparison[] comparisons = state.Comparisons.Where(item => File.Exists(item.BaselinePath) && File.Exists(item.OtherPath))
            .OrderByDescending(item => item.SavedUtc).Take(20).ToArray();
        return (recent, last, comparisons, warnings);

        void Scan(string directory)
        {
            try { foreach (string file in Directory.EnumerateFiles(directory, "frames*.csv")) Add(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("A capture folder could not be listed: " + ex.Message); }
        }

        void Add(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) paths.Add(System.IO.Path.GetFullPath(path));
        }
    }

    private static RecentCapture ReadSummary(string path)
    {
        string directory = System.IO.Path.GetDirectoryName(path)!;
        string scenario = "", mode = "";
        DateTime? started = null;
        var warnings = new List<string>();
        foreach (string name in new[] { "session.json", "recorder.json", "configuration.json" })
        {
            string metadata = System.IO.Path.Combine(directory, name);
            if (!File.Exists(metadata)) continue;
            try
            {
                if (new FileInfo(metadata).Length > 4 * 1024 * 1024) throw new InvalidDataException("Metadata exceeds 4 MB.");
                using var document = JsonDocument.Parse(File.ReadAllText(metadata));
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Metadata must contain a JSON object.");
                if (document.RootElement.TryGetProperty("Scenario", out var notes) && notes.ValueKind == JsonValueKind.String)
                    scenario = notes.GetString() ?? scenario;
                if (document.RootElement.TryGetProperty("Mode", out var captureMode) && captureMode.ValueKind == JsonValueKind.String)
                    mode = captureMode.GetString() ?? mode;
                if (document.RootElement.TryGetProperty("StartUtc", out var timestamp) && timestamp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
                    started = utc.UtcDateTime;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { warnings.Add(name + ": " + ex.Message); }
        }
        if (started is null)
        {
            try
            {
                // Only inspect the first row; listing recordings never imports their full frame/CPU data.
                var first = Csv.Read(path).FirstOrDefault();
                if (first is not null && DateTimeOffset.TryParse(first.Text("UtcTimestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var utc))
                {
                    double elapsed = first.Number("ElapsedMs") ?? 0;
                    if (elapsed >= 0 && elapsed <= TimeSpan.MaxValue.TotalMilliseconds)
                        started = utc.UtcDateTime - TimeSpan.FromMilliseconds(elapsed);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
            { warnings.Add("Frame timestamp: " + ex.Message); }
        }
        DateTime modified = File.GetLastWriteTimeUtc(path);
        if (started is null) warnings.Add("Capture time unavailable; showing the file modification time.");
        return new(path, modified) { StartedUtc = started ?? modified, Scenario = scenario, Mode = mode,
            MetadataWarning = string.Join("\n", warnings) };
    }

    public void Remember(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("The frame recording is missing.", full);
        State current = ReadState();
        string[] paths = current.Paths.Prepend(full).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        Write(new State(full, paths, current.Comparisons));
    }

    public void RememberComparison(string baselinePath, string otherPath, AnalysisOptions options)
    {
        string baseline = System.IO.Path.GetFullPath(baselinePath), other = System.IO.Path.GetFullPath(otherPath);
        if (!File.Exists(baseline) || !File.Exists(other)) throw new FileNotFoundException("A comparison recording is missing.");
        State current = ReadState();
        string[] paths = new[] { baseline, other }.Concat(current.Paths).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
        SavedComparison[] comparisons = new[] { new SavedComparison(baseline, other, DateTime.UtcNow, options) }
            .Concat(current.Comparisons.Where(item => !string.Equals(item.BaselinePath, baseline, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.OtherPath, other, StringComparison.OrdinalIgnoreCase))).Take(20).ToArray();
        Write(new State(baseline, paths, comparisons));
    }

    private void Write(State state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stateFile)!);
        string temporary = stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, stateFile, true);
    }

    private State ReadState()
    {
        if (!File.Exists(stateFile)) return new(null, [], []);
        if (new FileInfo(stateFile).Length > 4 * 1024 * 1024) throw new InvalidDataException("Recent recordings file exceeds 4 MB.");
        State state = JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile))
            ?? throw new InvalidDataException("Recent recordings file is empty.");
        if (state.Comparisons?.Any(item => item is null || string.IsNullOrWhiteSpace(item.BaselinePath)
            || string.IsNullOrWhiteSpace(item.OtherPath) || item.Options is null) == true)
            throw new InvalidDataException("Saved comparison history contains an incomplete pair.");
        return new(state.LastOpened, state.Paths ?? [], state.Comparisons ?? []);
    }
}
