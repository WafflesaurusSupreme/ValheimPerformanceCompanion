namespace Companion.Core;

public sealed record Frame(double Seconds, double Milliseconds, DateTimeOffset Utc)
{
    public int Index { get; init; }
    public bool? Focused { get; set; }
    public bool? HasPlayer { get; set; }
    public int? Gc0 { get; set; }
    public double? CpuMs { get; set; }
    public double? GpuMs { get; set; }
    public string Context { get; set; } = "";
}
public sealed record MethodWindow(string Name, double Start, double End, double MainMs, long Calls, int SampleEvery);
public sealed record LogEvent(double? Seconds, string Category, string Message, int Line);
public sealed record Finding(string Title, string Confidence, string Evidence, string NextTest, double? Seconds = null);
public sealed record Metrics(int Frames, double Duration, double AverageFps, double MedianMs, double P95Ms,
    double P99Ms, double OnePercentLow, double WorstMs, int LagSpikes, int RelativeSpikes, double OverBudgetSeconds,
    int ExcludedFrames);
public sealed class Capture
{
    public string Directory { get; set; } = "";
    public string Name { get; set; } = "";
    public List<Frame> Frames { get; } = [];
    public List<MethodWindow> Methods { get; } = [];
    public List<LogEvent> Events { get; } = [];
    public List<CpuObservation> CpuSamples { get; } = [];
    public List<ModuleIdentity> Modules { get; } = [];
    public List<PatchIdentity> Patches { get; } = [];
    public List<CodeMethod> Code { get; } = [];
    public List<GcPhase> GcPhases { get; } = [];
    public List<ThreadWait> Waits { get; } = [];
    public List<DiskObservation> Disk { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset StartUtc { get; set; }
}
public sealed record AnalysisOptions(double BudgetMs = 16.6667, bool ExcludeUnfocused = true,
    bool ExcludeMenus = true, double StartSeconds = 0, double EndSeconds = double.MaxValue);
public sealed record Analysis(Capture Capture, AnalysisOptions Options, Metrics Metrics, IReadOnlyList<Frame> Included,
    IReadOnlyList<Finding> Findings)
{
    public IReadOnlyList<LagSpike> DetectedSpikes { get; init; } = [];
    public DiagnosticReport Diagnostics { get; set; } = new();
}
public sealed record LagSpike(Frame Frame, bool Large, bool Relative)
{
    public string Label => $"{Frame.Seconds:F3}s · {Frame.Milliseconds:F2} ms · {(Large ? "Large lag spike" : "Relative lag spike")}";
    public override string ToString() => Label;
}
