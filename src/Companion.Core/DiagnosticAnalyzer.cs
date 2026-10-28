using System.Globalization;
using System.Text.RegularExpressions;

namespace Companion.Core;

public static partial class DiagnosticAnalyzer
{
    public static DiagnosticReport Build(Analysis analysis)
    {
        Capture capture = analysis.Capture;
        var report = new DiagnosticReport(); report.Modules.AddRange(capture.Modules); report.Patches.AddRange(capture.Patches);
        report.Coverage.AddRange(capture.Warnings);
        report.Coverage.Add("Every detected interval is listed. Stack examples are ranked samples, not measured method durations. Method windows are inclusive and may span several frames.");
        var samples = capture.CpuSamples.OrderBy(s => s.Seconds).ToArray();
        int main = int.TryParse(capture.Metadata.GetValueOrDefault("MainThreadId"), CultureInfo.InvariantCulture, out int tid) ? tid : 0;
        var code = capture.Code.GroupBy(c => c.Type + "." + c.Method, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var usedCode = new HashSet<string>(StringComparer.Ordinal);
        var frames = capture.Frames.OrderBy(f => f.Seconds).ToArray();
        var previous = new Dictionary<int, Frame>();
        for (int i = 1; i < frames.Length; i++) previous[frames[i].Index] = frames[i - 1];
        var pauses = Pauses(capture.GcPhases);
        var gcFrames = analysis.Included.Where(f => previous.TryGetValue(f.Index, out Frame? old) && f.Gc0 > old.Gc0).ToArray();
        var gcSpikes = analysis.DetectedSpikes.Where(s => gcFrames.Any(f => f.Index == s.Frame.Index)).ToArray();
        if (gcFrames.Length > 0)
        {
            double[] spacing = gcFrames.Zip(gcFrames.Skip(1), (a, b) => b.Seconds - a.Seconds).ToArray();
            var evidence = new List<string> { $"{gcFrames.Length} intervals contain a collection-count increase; {gcSpikes.Length} of {analysis.DetectedSpikes.Count} detected spikes overlap these intervals." };
            if (spacing.Length > 0) evidence.Add($"Collection intervals range from {spacing.Min():F3} to {spacing.Max():F3}s.");
            evidence.Add($"{pauses.Count} complete native suspended-world intervals recorded. Counter changes alone do not measure pause duration or identify the allocator.");
            report.Issues.Add(new("GC-pattern", "Garbage collection", pauses.Count > 0 ? "Pause intervals measured; allocation source unresolved" : "Repeated association; allocation source unresolved",
                "Garbage collection and recurring frame delays", "Collections can interrupt managed execution. The code that happens to trigger a collection may differ from the code that created most of the garbage.",
                gcFrames.Select(f => f.Index).ToArray(), gcFrames[0].Seconds - gcFrames[0].Milliseconds / 1000, gcFrames[^1].Seconds, [], [], evidence,
                ["Allocation call stacks and allocation rates by code path are required to identify the main allocation producers."],
                ["Inspect collection pause overlap for each interval below.", "Compare the same route and world state in Monitor, Standard and Deep to measure recorder overhead.", "Use supported allocation instrumentation before prescribing allocation-reduction changes; do not blame the last allocating call alone."]));
        }
        int sequence = 0;
        foreach (LagSpike spike in analysis.DetectedSpikes)
        {
            Frame frame = spike.Frame; double start = frame.Seconds - frame.Milliseconds / 1000;
            var evidence = new List<string> { $"Game-loop interval {frame.Milliseconds:F4} ms at {frame.Seconds:F6}s; frame index {frame.Index}." };
            var gaps = new List<string>(); var stacks = new List<StackEvidence>();
            int first = LowerBound(samples, start), last = LowerBound(samples, frame.Seconds + 0.000000001);
            var groups = samples[first..last].GroupBy(s => (s.ThreadId, s.Stack)).OrderByDescending(g => g.Count()).ToArray();
            foreach (var group in groups.Take(12))
            {
                var ids = new HashSet<string>();
                foreach (string symbol in group.Key.Stack.Split(" ← ", StringSplitOptions.RemoveEmptyEntries))
                {
                    string name = SymbolName(symbol);
                    if (!code.TryGetValue(name, out CodeMethod[]? candidates)) continue;
                    string module = symbol.Split('!')[0];
                    CodeMethod[] matching = candidates.Where(c => c.Assembly.Replace('-', '_').Equals(module, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length > 0) candidates = matching;
                    else gaps.Add("Module identity was not resolved from the sampled symbol for " + name + "; attached IL candidates match the type and method name only.");
                    if (candidates.Length > 1) gaps.Add("Overload/module ambiguity for " + name + ": candidate IL bodies are attached; the sample does not select one.");
                    foreach (CodeMethod method in candidates) { string id = method.Mvid + ":" + method.Token; ids.Add(id); usedCode.Add(id); }
                }
                stacks.Add(new(group.Key.ThreadId, main == 0 ? "Role unavailable" : group.Key.ThreadId == main ? "Main thread" : "Worker/other thread", group.Count(), group.Key.Stack, ids.ToArray()));
            }
            if (first == last) gaps.Add("No CPU samples overlap this interval; the trace may not cover it or the process may have been off CPU.");
            else evidence.Add($"{last - first} CPU samples overlap across {groups.Select(g => g.Key.ThreadId).Distinct().Count()} threads. Top {stacks.Count} of {groups.Length} distinct stacks attached; all raw samples are available in diagnostic-evidence/cpu-samples.csv.");
            if (main == 0) gaps.Add("OS main-thread identity was not recorded; samples cannot be safely treated as main-thread costs.");
            if (frame.CpuMs.HasValue) evidence.Add($"Unity CPU counter: {frame.CpuMs:F3} ms (may describe a previous completed frame).");
            if (frame.GpuMs.HasValue) evidence.Add($"Unity GPU counter: {frame.GpuMs:F3} ms (may describe a previous completed frame).");
            double gcMs = UnionOverlap(pauses.Select(p => (p.Start, p.End)), start, frame.Seconds) * 1000;
            bool gcCount = previous.TryGetValue(frame.Index, out Frame? prior) && frame.Gc0 > prior.Gc0;
            if (gcCount) evidence.Add("Collection count increased across this interval; this is not a measured pause length.");
            if (gcMs > 0) evidence.Add($"Native GC suspended-world intervals overlap {gcMs:F3} ms of this game-loop interval (union, without double counting). Stop/restart overhead is outside this measurement.");
            var waits = capture.Waits.Where(w => w.ThreadId == main && w.Start < frame.Seconds && w.End > start).ToArray();
            if (waits.Length > 0) evidence.Add($"Main thread was off CPU for {UnionOverlap(waits.Select(w => (w.Start, w.End)), start, frame.Seconds) * 1000:F3} ms. Recorded switch-out states/reasons: {string.Join(", ", waits.Select(w => w.State + "/" + w.Reason).Distinct())}. This does not identify the lock owner or prove disk caused the wait.");
            else gaps.Add("No complete main-thread off-CPU intervals overlap; this is not proof that no wait occurred.");
            var io = capture.Disk.Where(d => d.Seconds >= start && d.Seconds <= frame.Seconds).ToArray();
            if (io.Length > 0) evidence.Add($"{io.Length} process disk completions overlap; {io.Sum(d => d.Bytes)} bytes. Asynchronous I/O correlation is not evidence of main-thread blocking.");
            var methods = capture.Methods.Where(m => m.Start < frame.Seconds && m.End > start && m.MainMs > 0).OrderByDescending(m => m.MainMs).Take(10).Select(m => new MethodEvidence(m.Name, m.Start, m.End, m.MainMs, m.Calls, m.SampleEvery)).ToArray();
            if (methods.Length > 0) evidence.Add("Attached method costs cover overlapping multi-frame windows. Parent and child times must not be summed or assigned entirely to this interval.");
            if (stacks.All(s => s.CodeIds.Count == 0)) gaps.Add("No matching managed IL is available for these stack examples. Native addresses require matching symbols; old captures need the original loaded assemblies or a new code snapshot.");
            if (gcCount && gcMs == 0) gaps.Add("Native GC pause timing is unavailable for this interval.");
            string category = gcMs > 0 || gcCount ? "GC-associated interval" : waits.Length > 0 ? "CPU/wait interval" : "Slow interval";
            report.Issues.Add(new("frame-" + (++sequence).ToString("D4"), category, gcMs > 0 ? "GC pause contribution measured; underlying allocator unresolved" : "Observed work; root cause requires code review",
                $"{frame.Milliseconds:F1} ms at {frame.Seconds:F3}s", "Inspect the complete call path and surrounding state. A hot method can be necessary work, a repeated operation, or instrumentation overhead; execution alone is not proof of inefficient code.",
                [frame.Index], start, frame.Seconds, stacks, methods, evidence, gaps.Distinct().ToArray(),
                ["Use the attached method identities and IL to inspect calls, allocations, loops, and cache invalidation. Identify the exact unnecessary operation before editing.", "Inspect every relevant Harmony patch and caller; distinguish vanilla work from added work and interactions between mods.", "Reproduce with the same route, save, weather and settings; compare before/after frame distributions and verify gameplay behavior. Do not disable mods or alter saves automatically."]));
        }
        var slowWindows = analysis.Included.GroupBy(f => (int)f.Seconds).Where(g => g.Count() >= 10 && g.Average(f => f.Milliseconds) > analysis.Options.BudgetMs).ToArray();
        if (slowWindows.Length > 0)
        {
            var bucketIds = slowWindows.Select(g => g.Key).ToHashSet();
            var sustainedStacks = new List<StackEvidence>();
            foreach (var group in samples.Where(s => bucketIds.Contains((int)s.Seconds)).GroupBy(s => (s.ThreadId, s.Stack)).OrderByDescending(g => g.Count()).Take(20))
            {
                var ids = new HashSet<string>();
                foreach (string symbol in group.Key.Stack.Split(" ← ", StringSplitOptions.RemoveEmptyEntries))
                    if (code.TryGetValue(SymbolName(symbol), out CodeMethod[]? candidates))
                        foreach (CodeMethod method in candidates) { string id = method.Mvid + ":" + method.Token; ids.Add(id); usedCode.Add(id); }
                sustainedStacks.Add(new(group.Key.ThreadId, main == 0 ? "Role unavailable" : group.Key.ThreadId == main ? "Main thread" : "Worker/other thread", group.Count(), group.Key.Stack, ids.ToArray()));
            }
            report.Issues.Add(new("sustained-load", "Sustained load", "Observed; root cause unresolved", $"{slowWindows.Length} one-second buckets exceed the mean frame budget", "Sustained work may lower FPS even without a large spike.", slowWindows.SelectMany(g => g.Select(f => f.Index)).ToArray(), slowWindows.Min(g => (double)g.Key), slowWindows.Max(g => g.Key) + 1, sustainedStacks, [],
                slowWindows.Select(g => $"{g.Key}–{g.Key + 1}s: {g.Count()} intervals, mean {g.Average(f => f.Milliseconds):F3} ms.").ToArray(), ["Each bucket can also contain individual spikes; it is not an independent additive cost.", "Code candidates match sampled type/method names; overloads and module identities require verification. Samples cover whole buckets and can include excluded frames."], ["Compare CPU paths and GPU observations across the sustained interval; inspect repeated work and allocation rates."]));
        }
        // Include patch bodies associated with resolved targets, but never treat
        // their presence as evidence that they caused the measured cost.
        foreach (PatchIdentity patch in capture.Patches.Where(p => usedCode.Contains(p.TargetMvid + ":" + p.TargetToken))) usedCode.Add(patch.PatchMvid + ":" + patch.PatchToken);
        report.Code.AddRange(capture.Code.Where(c => usedCode.Contains(c.Mvid + ":" + c.Token)));
        if (report.Issues.Count == 0) report.Coverage.Add("No detected performance problem in this selection. This does not establish that every gameplay path is healthy.");
        return report;
    }
    public static List<GcPause> Pauses(IEnumerable<GcPhase> phases)
    {
        var starts = new Dictionary<int, double>(); var result = new List<GcPause>();
        foreach (GcPhase phase in phases.OrderBy(p => p.Seconds))
        {
            if (phase.Phase == 7) starts[phase.ThreadId] = phase.Seconds;
            else if (phase.Phase == 8 && starts.Remove(phase.ThreadId, out double start) && phase.Seconds >= start)
                result.Add(new(start, phase.Seconds, phase.ThreadId, "Mono POST_STOP_WORLD to PRE_START_WORLD"));
        }
        return result;
    }
    public static double UnionOverlap(IEnumerable<(double Start, double End)> intervals, double start, double end)
    {
        double total = 0, last = start;
        foreach (var interval in intervals.OrderBy(i => i.Start)) { double a = Math.Max(last, Math.Max(start, interval.Start)), b = Math.Min(end, interval.End); if (b > a) { total += b - a; last = b; } }
        return total;
    }
    internal static string SymbolName(string symbol)
    {
        int bang = symbol.IndexOf('!'); string name = (bang < 0 ? symbol : symbol[(bang + 1)..]).TrimStart('.');
        Match dynamic = DynamicMethod().Match(name);
        if (dynamic.Success) return name[..dynamic.Index] + "." + dynamic.Groups[1].Value;
        int args = name.IndexOf('('); return args < 0 ? name : name[..args];
    }
    [GeneratedRegex(@"\.DMD<[^:>]+::([^>]+)>", RegexOptions.CultureInvariant)] private static partial Regex DynamicMethod();
    private static int LowerBound(CpuObservation[] values, double seconds)
    { int low = 0, high = values.Length; while (low < high) { int mid = low + (high - low) / 2; if (values[mid].Seconds < seconds) low = mid + 1; else high = mid; } return low; }
}
