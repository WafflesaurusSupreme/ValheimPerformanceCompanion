using System.IO;
using System.Text.Json;
using Companion.Core;

namespace Companion.App;

public sealed record RecentCapture(string Path, DateTime ModifiedUtc)
{
    public string Label => $"{ModifiedUtc.ToLocalTime():g} · {CaptureName(Path)}";
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

    public (IReadOnlyList<RecentCapture> Recent, string? LastOpened, IReadOnlyList<SavedComparison> Comparisons) Read(string? profileDirectory)
    {
        State state = ReadState();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in state.Paths) Add(path);
        if (profileDirectory is not null)
        {
            string root = System.IO.Path.Combine(profileDirectory, "BepInEx", "PerformanceCompanion", "Captures");
            if (Directory.Exists(root))
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string file in Directory.EnumerateFiles(directory, "frames*.csv")) Add(file);
                }
        }
        string? last = state.LastOpened is not null && paths.Contains(state.LastOpened) ? state.LastOpened : null;
        var recent = paths.Select(path => new RecentCapture(path, File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(item => string.Equals(item.Path, last, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.ModifiedUtc).Take(30).ToArray();
        SavedComparison[] comparisons = state.Comparisons.Where(item => File.Exists(item.BaselinePath) && File.Exists(item.OtherPath))
            .OrderByDescending(item => item.SavedUtc).Take(20).ToArray();
        return (recent, last, comparisons);

        void Add(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) paths.Add(System.IO.Path.GetFullPath(path));
        }
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
        State state = JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile))
            ?? throw new InvalidDataException("Recent recordings file is empty.");
        return new(state.LastOpened, state.Paths ?? [], state.Comparisons ?? []);
    }
}
