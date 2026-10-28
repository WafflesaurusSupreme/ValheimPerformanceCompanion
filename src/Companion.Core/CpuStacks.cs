using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace Companion.Core;

public sealed record CpuHotspot(string Name, double Weight, int Samples);
public static partial class CpuStacks
{
    public static IReadOnlyList<CpuHotspot> Read(string path, double startMs = 0, double endMs = double.MaxValue, int? processId = null)
    {
        using var zip = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("CPU stack archive contains no XML.");
        if (entry.Length > 128L * 1024 * 1024) throw new InvalidDataException("CPU stack XML exceeds 128 MB.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128L * 1024 * 1024 });
        XDocument document = XDocument.Load(reader);
        var frames = document.Descendants("Frame").Where(e => e.Attribute("ID") != null).ToDictionary(e => (string)e.Attribute("ID")!, e => e.Value);
        var stacks = document.Descendants("Stack").Where(e => e.Attribute("ID") != null).ToDictionary(e => (string)e.Attribute("ID")!, e => (Frame: (string?)e.Attribute("FrameID") ?? "", Caller: (string?)e.Attribute("CallerID") ?? "-1"));
        var processCache = new Dictionary<string, int?>();
        int? ProcessFor(string stack)
        {
            if (processCache.TryGetValue(stack, out int? cached)) return cached;
            string current = stack;
            for (int depth = 0; depth < 512 && stacks.TryGetValue(current, out var node); depth++)
            {
                if (frames.TryGetValue(node.Frame, out string? name))
                { Match match = ProcessFrame().Match(name); if (match.Success) { int found = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); processCache[stack] = found; return found; } }
                current = node.Caller;
            }
            processCache[stack] = null; return null;
        }
        var totals = new Dictionary<string, (double Weight, int Samples)>();
        foreach (XElement sample in document.Descendants("Sample"))
        {
            double time = Number(sample.Attribute("Time")?.Value ?? sample.Attribute("TimeRelativeMSec")?.Value);
            if (time < startMs || time > endMs) continue;
            string stack = (string?)sample.Attribute("StackID") ?? "";
            if (processId.HasValue && ProcessFor(stack) != processId) continue;
            string name = stacks.TryGetValue(stack, out var leaf) && frames.TryGetValue(leaf.Frame, out string? value) ? value : "Unresolved frame";
            double weight = Number(sample.Attribute("Metric")?.Value ?? "1");
            var old = totals.GetValueOrDefault(name); totals[name] = (old.Weight + weight, old.Samples + 1);
        }
        return totals.Select(t => new CpuHotspot(t.Key, t.Value.Weight, t.Value.Samples)).OrderByDescending(t => t.Weight).Take(100).ToArray();
    }
    private static double Number(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) ? parsed : 0;
    [GeneratedRegex(@"^Process(?:32|64)?\s+.*?\((\d+)\)", RegexOptions.CultureInvariant)] private static partial Regex ProcessFrame();
}
