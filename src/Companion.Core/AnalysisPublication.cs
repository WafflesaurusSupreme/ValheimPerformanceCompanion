using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Collector;

namespace Companion.Core;

internal sealed record AnalysisTransaction(int Version, string Generation, int ProcessId, DateTimeOffset StartUtc,
    string SourceTrace, string SourceSha256, Dictionary<string, string> Outputs);

internal static partial class AnalysisPublication
{
    private static readonly string[] outputs = ["cpu-samples.csv", "thread-waits.csv", "disk-io.csv", "trace-coverage.json", "cpu-analysis.json"];
    private const string Manifest = "analysis-pending.json";

    internal static bool IsComplete(string root, int? processId = null)
    {
        try
        {
            if (outputs.Any(name => !RegularFile(Path.Combine(root, name)))) return false;
            TraceSummary summary = CollectorStaging.ReadReceipt<TraceSummary>(Path.Combine(root, "cpu-analysis.json"));
            if (summary.Samples <= 0 || summary.ProcessId <= 0 || summary.MissingStacks < 0 || summary.MissingStacks > summary.Samples
                || summary.TraceEndUtc < summary.TraceStartUtc || processId.HasValue && summary.ProcessId != processId.Value) return false;
            if (File.Exists(Path.Combine(root, Manifest)))
            {
                AnalysisTransaction transaction = ReadTransaction(root, summary.ProcessId);
                if (outputs.Any(name => Hash(Path.Combine(root, name)) != transaction.Outputs[name])) return false;
            }
            return true; // Legacy complete exports have the same five output files and a valid final summary.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        { return false; }
    }

    internal static bool TryResume(string root, int processId, out TraceSummary? summary)
    {
        summary = null;
        if (!File.Exists(Path.Combine(root, Manifest))) return false;
        AnalysisTransaction transaction = ReadTransaction(root, processId);
        string generation = Path.Combine(root, transaction.Generation);
        var directory = new DirectoryInfo(generation);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Retained analysis generation is unavailable.");
        foreach (string name in outputs)
        {
            string file = Path.Combine(generation, name);
            if (!RegularFile(file) || Hash(file) != transaction.Outputs[name]) throw new IOException("Retained analysis generation is incomplete or changed.");
        }
        Publish(root, generation, transaction);
        summary = CollectorStaging.ReadReceipt<TraceSummary>(Path.Combine(root, "cpu-analysis.json"));
        return true;
    }

    internal static void Commit(string root, string generation, int processId, DateTimeOffset startUtc, string sourceTrace)
    {
        string leaf = Path.GetFileName(generation);
        if (!GenerationName().IsMatch(leaf) || !string.Equals(Path.GetDirectoryName(generation), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Analysis generation must be a direct, named capture subdirectory.");
        var hashes = outputs.ToDictionary(name => name, name => Hash(Path.Combine(generation, name)), StringComparer.Ordinal);
        var transaction = new AnalysisTransaction(1, leaf, processId, startUtc, Path.GetFileName(sourceTrace), Hash(sourceTrace), hashes);
        string target = Path.Combine(root, Manifest);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(transaction));
        File.Move(temporary, target); // A pre-existing transaction must be resumed, never replaced.
        Publish(root, generation, transaction);
    }

    private static AnalysisTransaction ReadTransaction(string root, int processId)
    {
        AnalysisTransaction transaction = CollectorStaging.ReadReceipt<AnalysisTransaction>(Path.Combine(root, Manifest));
        if (transaction.Version != 1 || transaction.ProcessId != processId || string.IsNullOrEmpty(transaction.Generation) || !GenerationName().IsMatch(transaction.Generation)
            || transaction.SourceTrace is not ("cpu.etl" or "cpu.etl.zip") || string.IsNullOrEmpty(transaction.SourceSha256) || !Sha256().IsMatch(transaction.SourceSha256)
            || transaction.Outputs is null || transaction.Outputs.Count != outputs.Length
            || outputs.Any(name => !transaction.Outputs.TryGetValue(name, out string? hash) || hash is null || !Sha256().IsMatch(hash)))
            throw new InvalidDataException("Invalid retained analysis transaction.");
        using JsonDocument recorder = JsonDocument.Parse(ReadRecorderMetadata(root));
        if (ReadRecorderClock(recorder.RootElement) != transaction.StartUtc)
            throw new IOException("The recorder clock anchor changed after analysis began; existing evidence was preserved.");
        string source = Path.Combine(root, transaction.SourceTrace);
        if (File.Exists(source) && Hash(source) != transaction.SourceSha256) throw new IOException("The input trace changed after analysis began; existing evidence was preserved.");
        return transaction;
    }

    internal static byte[] ReadRecorderMetadata(string root)
    {
        string path = Path.Combine(root, "recorder.json");
        if (!RegularFile(path)) throw new IOException("Recorder metadata must be a regular file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException("Recorder metadata exceeds its size limit.");
        byte[] data = new byte[checked((int)stream.Length)]; stream.ReadExactly(data); return data;
    }

    internal static DateTimeOffset ReadRecorderClock(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("StartUtc", out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(property.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset clock))
            throw new InvalidDataException("Recorder metadata has no valid clock anchor.");
        return clock;
    }

    private static void Publish(string root, string generation, AnalysisTransaction transaction)
    {
        // Check all pre-existing destinations before adding anything; never overwrite unrelated evidence.
        foreach (string name in outputs)
        {
            string destination = Path.Combine(root, name);
            if (File.Exists(destination) && (!RegularFile(destination) || Hash(destination) != transaction.Outputs[name]))
                throw new IOException("Existing analysis output differs from the retained generation: " + name + ". Both copies are preserved.");
        }
        foreach (string name in outputs) // cpu-analysis.json is the final commit receipt.
        {
            string destination = Path.Combine(root, name);
            if (File.Exists(destination)) continue;
            string staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(Path.Combine(generation, name), staged);
            File.Move(staged, destination);
        }
    }

    private static bool RegularFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }
    private static string Hash(string path)
    {
        if (!RegularFile(path)) throw new IOException("Analysis input/output must be a regular file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    [GeneratedRegex("^analysis-[a-f0-9]{32}$", RegexOptions.CultureInvariant)] private static partial Regex GenerationName();
    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256();
}
