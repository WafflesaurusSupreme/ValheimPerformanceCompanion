using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Companion.Core;

public static partial class ReportExporter
{
    public static string Html(Analysis analysis)
    {
        string E(string value) => WebUtility.HtmlEncode(Redact(value));
        Metrics m = analysis.Metrics;
        string graph = string.Join(" ", Envelope(analysis.Included, 900).Select((f, i) => $"{i.ToString(CultureInfo.InvariantCulture)},{(150 - Math.Min(150, f / Math.Max(m.P99Ms * 1.5, 33.3) * 150)).ToString("F2", CultureInfo.InvariantCulture)}"));
        return "<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Valheim performance report</title>" +
            "<style>body{background:#10151d;color:#e6ecf5;font:16px system-ui;margin:40px auto;padding:0 24px;max-width:1000px}h1{font-size:32px}small,p{color:#a9b5c8;line-height:1.6}.cards{display:flex;gap:16px;flex-wrap:wrap}.card,article{padding:20px;background:#1a2230;border:1px solid #293649;border-radius:14px;margin:12px 0}.value{font-size:30px;color:#6ee7c7}svg{width:100%;height:200px;background:#151d29}li{margin:10px 0}pre{white-space:pre-wrap}b{color:#e6ecf5}</style>" +
            $"<small>PERFORMANCE COMPANION · LOCAL REPORT</small><h1>{E(analysis.Capture.Name)}</h1><p>Game-loop frame intervals · {m.Duration:F1}s included · {m.ExcludedFrames} frames excluded</p>" +
            $"<div class='cards'><div class='card'>Average FPS<div class='value'>{m.AverageFps:F1}</div></div><div class='card'>1% low<div class='value'>{m.OnePercentLow:F1}</div></div><div class='card'>P99 frame time<div class='value'>{m.P99Ms:F2} ms</div></div><div class='card'>Large lag spikes<div class='value'>{m.LagSpikes}</div></div></div>" +
            $"<svg viewBox='0 0 900 160' preserveAspectRatio='none'><polyline points='{graph}' fill='none' stroke='#6ee7c7' stroke-width='1.5'/></svg><p>Graph preserves maximum frame time per bucket; high spikes are clipped for readability.</p>" +
            "<h2>Automatically detected lag spikes</h2><p>Up to 100 largest intervals shown; all detected intervals are included in lag-spikes.csv.</p><ul>" + string.Join("", analysis.DetectedSpikes.OrderByDescending(s => s.Frame.Milliseconds).Take(100).Select(s => "<li>" + E(s.Label) + "</li>")) + "</ul>" +
            string.Join("", analysis.Findings.Select(f => $"<article><small>{E(f.Confidence)}</small><h2>{E(f.Title)}</h2><p>{E(f.Evidence)}</p><p><b>Next test:</b> {E(f.NextTest)}</p></article>")) +
            DiagnosticExporter.Html(analysis.Diagnostics) + "<h2>Capture quality and limits</h2><ul>" + string.Join("", analysis.Capture.Warnings.Select(w => "<li>" + E(w) + "</li>")) + "</ul>" +
            $"<p>Frame budget {analysis.Options.BudgetMs:F2} ms. Average FPS = valid intervals / sum of their duration. 1% low = 1000 / mean of slowest ceil(1%) intervals. Percentiles use linear interpolation. Lag spike threshold = max(50 ms, 3 × budget). Relative spikes exceed max(8 ms, 2.5 × preceding 60-frame median). Time over budget sums the full duration of over-budget intervals. Missing state cannot be filtered. CPU method measurements are inclusive and overlap.</p>" +
            "<h2>Relevant log events</h2>" + string.Join("", analysis.Capture.Events.Take(150).Select(e => $"<p><b>{E(e.Category)} · {(e.Seconds?.ToString("F2", CultureInfo.InvariantCulture) ?? "time unknown")}</b><br>{E(e.Message)}</p>")) +
            "<p>Paths and common network/account identifiers are automatically redacted. Review before sharing; automatic redaction cannot identify every private string.</p></html>";
    }
    public static void Export(Analysis analysis, string destination, bool includeRaw)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "report.html", Html(analysis));
        Write(zip, "lag-spikes.csv", "Seconds,FrameIntervalMs,Large,Relative\n" + string.Join("\n", analysis.DetectedSpikes.Select(s =>
            s.Frame.Seconds.ToString("F6", CultureInfo.InvariantCulture) + "," + s.Frame.Milliseconds.ToString("F6", CultureInfo.InvariantCulture) + "," + s.Large + "," + s.Relative)));
        Write(zip, "metrics.json", JsonSerializer.Serialize(new { Version = 1, analysis.Metrics, analysis.Options,
            Findings = analysis.Findings.Select(f => new { f.Title, f.Confidence, Evidence = Redact(f.Evidence), NextTest = Redact(f.NextTest), f.Seconds }),
            Warnings = analysis.Capture.Warnings.Select(Redact) }, new JsonSerializerOptions { WriteIndented = true }));
        Write(zip, "events.csv", "Seconds,Category,Message\n" + string.Join("\n", analysis.Capture.Events.Select(e =>
            (e.Seconds?.ToString("F4", CultureInfo.InvariantCulture) ?? "") + "," + Csv.Escape(e.Category) + "," + Csv.Escape(Redact(e.Message)))));
        if (includeRaw)
        {
            long total = 0;
            foreach (string path in Directory.GetFiles(analysis.Capture.Directory))
            {
                string name = Path.GetFileName(path);
                if (!(name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))) continue;
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Raw export does not follow file links.");
                total += info.Length;
                if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Raw export exceeds 2 GB. Share selected trace files separately.");
                zip.CreateEntryFromFile(path, "raw/" + name, CompressionLevel.Fastest);
            }
        }
        DiagnosticExporter.Export(zip, analysis);
    }
    private static void Write(ZipArchive zip, string name, string content)
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
    public static string Redact(string value)
    {
        value = UserPath().Replace(value, @"C:\Users\[user]");
        value = IpAddress().Replace(value, "[network address]");
        value = SteamId().Replace(value, "[account id]");
        return Secrets().Replace(value, "$1=[redacted]");
    }
    public static double[] Envelope(IReadOnlyList<Frame> frames, int buckets)
    {
        if (frames.Count == 0) return [];
        double start = frames[0].Seconds, span = Math.Max(.001, frames[^1].Seconds - start);
        var values = new double[buckets];
        foreach (Frame frame in frames) { int i = Math.Clamp((int)((frame.Seconds - start) / span * (buckets - 1)), 0, buckets - 1); values[i] = Math.Max(values[i], frame.Milliseconds); }
        return values;
    }
    [GeneratedRegex(@"[A-Za-z]:[\\/]Users[\\/][^\\/\s""<>]+", RegexOptions.IgnoreCase)] private static partial Regex UserPath();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b")] private static partial Regex IpAddress();
    [GeneratedRegex(@"\b7656119\d{10}\b")] private static partial Regex SteamId();
    [GeneratedRegex(@"\b(password|token|ticket|secret|authorization)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)] private static partial Regex Secrets();
}
