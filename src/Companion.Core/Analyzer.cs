namespace Companion.Core;

public static class Analyzer
{
    public static Analysis Analyze(Capture capture, AnalysisOptions options)
    {
        if (!double.IsFinite(options.BudgetMs) || options.BudgetMs <= 0 || options.EndSeconds <= options.StartSeconds)
            throw new ArgumentException("Choose a positive frame budget and a valid time range.");
        Frame[] range = capture.Frames.Where(f => f.Seconds >= options.StartSeconds && f.Seconds <= options.EndSeconds && f.Milliseconds > 0).ToArray();
        Frame[] included = range.Where(f => (!options.ExcludeUnfocused || f.Focused != false) && (!options.ExcludeMenus || f.HasPlayer != false)).ToArray();
        double[] sorted = included.Select(f => f.Milliseconds).Order().ToArray();
        double duration = sorted.Sum() / 1000;
        int tail = Math.Max(1, (int)Math.Ceiling(sorted.Length * .01));
        int relative = 0;
        var detected = new List<LagSpike>();
        var window = new List<double>(61);
        for (int i = 0; i < included.Length; i++)
        {
            bool isRelative = false;
            if (window.Count >= 10)
            {
                double median = window.Count % 2 == 0 ? (window[window.Count / 2 - 1] + window[window.Count / 2]) / 2 : window[window.Count / 2];
                if (included[i].Milliseconds > Math.Max(8, median * 2.5)) { relative++; isRelative = true; }
            }
            bool large = included[i].Milliseconds > Math.Max(50, options.BudgetMs * 3);
            if (large || isRelative) detected.Add(new(included[i], large, isRelative));
            if (i >= 60) window.Remove(included[i - 60].Milliseconds);
            int position = window.BinarySearch(included[i].Milliseconds);
            window.Insert(position < 0 ? ~position : position, included[i].Milliseconds);
        }
        var metrics = new Metrics(sorted.Length, duration, duration > 0 ? sorted.Length / duration : 0,
            Percentile(sorted, .5), Percentile(sorted, .95), Percentile(sorted, .99),
            sorted.Length > 0 ? 1000 / sorted.TakeLast(tail).Average() : 0, sorted.LastOrDefault(),
            sorted.Count(x => x > Math.Max(50, options.BudgetMs * 3)), relative,
            sorted.Where(x => x > options.BudgetMs).Sum() / 1000, range.Length - included.Length);
        var findings = new List<Finding>();
        if (included.Length == 0) findings.Add(new("No frames in this selection", "Observed", "The selected filters exclude all valid intervals.", "Include menus/unfocused time or widen the range."));
        var seen = new HashSet<string>();
        foreach (Frame frame in included.OrderByDescending(f => f.Milliseconds).Take(12))
        {
            double start = frame.Seconds - frame.Milliseconds / 1000;
            var cpuGroup = capture.CpuSamples.Where(s => s.Seconds >= start && s.Seconds <= frame.Seconds).GroupBy(s => s.Leaf).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (cpuGroup is not null && cpuGroup.Count() >= 3 && seen.Add("CPU:" + cpuGroup.Key))
                findings.Add(new("CPU samples overlap a slow interval", "Associated", $"At {frame.Seconds:F3}s: {frame.Milliseconds:F1} ms game-loop interval. {cpuGroup.Count()} ETW samples have leaf {cpuGroup.Key}. Samples use the ETW absolute timestamp and recorder UTC anchor; they are not method durations or proof of cause.",
                    "Inspect the selected frame's call stacks and repeat the same scenario with one controlled change.", frame.Seconds));
            MethodWindow? method = capture.Methods.Where(m => m.Start < frame.Seconds && m.End >= start && m.MainMs > 3).OrderByDescending(m => m.MainMs).FirstOrDefault();
            if (method is not null && seen.Add(method.Name))
                findings.Add(new("CPU work overlaps a slow frame", "Associated", $"At {frame.Seconds:F2}s: {frame.Milliseconds:F1} ms frame. {method.Name} measured {method.MainMs:F1} ms inclusive main-thread time across {method.End - method.Start:F3}s ({method.Calls} calls; sample every {method.SampleEvery}). This is a window, not exact per-frame attribution.",
                    "Repeat the same scenario with one controlled change; inspect the overlapping PerfView CPU stacks.", frame.Seconds));
            Frame? previous = capture.Frames.LastOrDefault(f => f.Seconds < frame.Seconds);
            if (frame.Gc0 is int gc && previous?.Gc0 is int old && gc > old && seen.Add("gc"))
                findings.Add(new("Garbage collection near a lag spike", "Associated", $"A collection-count increase overlaps the {frame.Milliseconds:F1} ms interval at {frame.Seconds:F2}s. This does not measure pause duration.", "Compare allocation-heavy activity in the same interval using a Deep capture.", frame.Seconds));
        }
        int errors = capture.Events.Count(e => e.Category == "Error");
        if (errors > 0) findings.Add(new("Errors recorded in the game log", "Observed", $"{errors} error/exception lines found. Untimestamped lines cannot be assigned to a particular lag spike or assumed to belong to this capture.", "Inspect the error list and repeat the scenario after addressing the recurring error."));
        int gpuCount = included.Count(f => f.GpuMs > options.BudgetMs);
        if (gpuCount > 10) findings.Add(new("GPU timing repeatedly exceeds the budget", "Associated", $"{gpuCount} available GPU timing observations exceed {options.BudgetMs:F2} ms. Unity timing can lag the recorded interval.", "Repeat with lower render resolution while holding the route and other settings constant."));
        if (metrics.LagSpikes > 0 && findings.All(f => f.Seconds is null)) findings.Add(new("Lag spikes measured; cause unresolved", "Observed", $"{metrics.LagSpikes} frames exceeded {Math.Max(50, options.BudgetMs * 3):F1} ms. The available evidence does not establish a cause.", "Repeat the scenario with a Deep capture to collect more evidence automatically."));
        if (metrics.LagSpikes == 0 && included.Length > 0) findings.Add(new("No large lag spikes in the selected interval", "Observed", $"No frame exceeded {Math.Max(50, options.BudgetMs * 3):F1} ms; {relative} relative spikes were detected. Smaller pacing problems may still be perceptible.", "Inspect automatically detected relative lag spikes or select a tighter time range."));
        var analysis = new Analysis(capture, options, metrics, included, findings) { DetectedSpikes = detected };
        analysis.Diagnostics = DiagnosticAnalyzer.Build(analysis);
        return analysis;
    }
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        double index = (sorted.Length - 1) * p; int low = (int)index;
        return sorted[low] + (sorted[Math.Min(low + 1, sorted.Length - 1)] - sorted[low]) * (index - low);
    }
    public static string Compare(Analysis baseline, Analysis candidate)
    {
        var a = baseline.Metrics; var b = candidate.Metrics;
        var differences = baseline.Capture.Metadata.Keys.Union(candidate.Capture.Metadata.Keys)
            .Where(k => new[] { "GameVersion", "PluginVersion", "ProfileFingerprint", "ConfigurationFingerprint", "Graphics", "Mode", "Scenario" }.Contains(k))
            .Where(k => baseline.Capture.Metadata.GetValueOrDefault(k) != candidate.Capture.Metadata.GetValueOrDefault(k)).ToArray();
        return $"Baseline → comparison\nAverage FPS: {a.AverageFps:F1} → {b.AverageFps:F1}\n99th percentile: {a.P99Ms:F2} → {b.P99Ms:F2} ms\n1% low: {a.OnePercentLow:F1} → {b.OnePercentLow:F1} FPS\nLag spikes/min: {a.LagSpikes / Math.Max(a.Duration / 60, .001):F1} → {b.LagSpikes / Math.Max(b.Duration / 60, .001):F1}\n\n" +
            (differences.Length > 0 ? "Recorded differences: " + string.Join(", ", differences) + ".\n" : "No differences in the available comparison metadata. Missing metadata is not proof of equivalent conditions.\n") +
            "An observational comparison is not a controlled benchmark. Match route, weather, world state, graphics settings and capture mode.";
    }
}
