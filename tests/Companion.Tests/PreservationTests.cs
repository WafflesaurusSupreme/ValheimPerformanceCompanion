using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Companion.Core;
using Plan = ValheimPerformanceCompanion.Recorder.ParticleCensusPlan;

internal static class PreservationTests
{
    public static void Run(string root)
    {
        string output = Path.Combine(root, "preservation"); Directory.CreateDirectory(output);
        string current = Path.Combine(AppContext.BaseDirectory, "Recorder", RecorderSetup.FileName);
        string baseline = Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorder-census-baseline.dll");
        string stale = Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorder-without-census.dll");
        bool exactBaseline = File.Exists(baseline) && Environment.GetEnvironmentVariable("VPC_PUBLIC_FIXTURE_TEST") != "1";
        if (exactBaseline) Assert(Hash(baseline) == "49050949BCCD41B4569C76F486827E16BB429E4EBBDBF9A33B3DBFDCE2231FF9", "Preserved baseline identity changed.");
        else
        {
            // Public CI tests a generated metadata-version fixture; exact historical bytes stay local.
            baseline = Path.Combine(output, "generated-capable-0.1.14.dll");
            byte[] image = File.ReadAllBytes(current);
            using (var pe = new PEReader(new MemoryStream(image, false)))
            {
                int version = pe.PEHeaders.MetadataStartOffset + pe.GetMetadataReader().GetTableMetadataOffset(TableIndex.Assembly) + 4;
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(version), 0);
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(version + 2), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(version + 4), 14);
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(version + 6), 0);
            }
            File.WriteAllBytes(baseline, image);
        }
        if (!File.Exists(stale) || Environment.GetEnvironmentVariable("VPC_PUBLIC_FIXTURE_TEST") == "1")
            stale = typeof(RecorderSetup).Assembly.Location; // A managed assembly with none of the recorder capabilities.
        var old = RecorderCapabilities.Read(baseline); var next = RecorderCapabilities.Read(current); var lost = RecorderCapabilities.Read(stale);
        Assert(old.ParticleCensus && old.DisableCleanup && old.EarlyCommandValidation, "Baseline IL capabilities were not recognized.");
        Assert(next.Preserves(old) && next.ParticleCensus && next.DisableCleanup && next.EarlyCommandValidation, "Current recorder does not preserve the installed baseline.");
        Assert(!lost.ParticleCensus && !lost.DisableCleanup && !lost.EarlyCommandValidation && !lost.Preserves(old), "Feature-losing bundle was not identified.");

        var target = FixtureTarget("upgrade"); string bundle = Bundle("new", current);
        string installed = Path.Combine(target.BepInExRoot, "plugins", "ValheimPerformanceCompanion", RecorderSetup.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!); File.Copy(baseline, installed);
        string original = Hash(installed);
        Reject(() => RecorderSetup.Ensure(target, Bundle("stale", stale), () => false));
        Assert(Hash(installed) == original, "Rejected downgrade changed the recorder.");
        Assert(RecorderSetup.Ensure(target, bundle, () => false).Ready, "Preserving upgrade failed.");
        string transaction = Directory.GetFiles(Path.Combine(target.BepInExRoot, "PerformanceCompanion", "SetupTransactions"), "*.json").Single();
        using (var receipt = JsonDocument.Parse(File.ReadAllText(transaction)))
        {
            Assert(receipt.RootElement.GetProperty("State").GetString() == "InstalledAndVerified", "Transaction did not complete.");
            Assert(receipt.RootElement.GetProperty("PreviousSHA256").GetString() == original, "Transaction lost its preimage.");
            Assert(Hash(receipt.RootElement.GetProperty("Backup").GetString()!) == original, "Backup is not the exact prior recorder.");
        }
        byte[] sameRevision = File.ReadAllBytes(current).Concat(new byte[] { 0 }).ToArray(); File.WriteAllBytes(installed, sameRevision);
        Assert(!RecorderSetup.Ensure(target, bundle, () => false).Ready && Hash(installed) == Convert.ToHexString(SHA256.HashData(sameRevision)), "Different same-revision recorder was silently overwritten.");
        File.Copy(baseline, installed, true); int checks = 0;
        Reject(() => RecorderSetup.Ensure(target, bundle, () => { if (++checks == 2) File.Copy(stale, installed, true); return false; }));
        Assert(Hash(installed) == Hash(stale), "A changed setup preimage was overwritten.");

        var plan = new Plan(1000); Plan.Attempt? attempt = null;
        for (int frame = 0; frame <= 42; frame++)
        {
            var request = plan.Observe(new(1000 + frame * 50, frame, 1, Plan.SceneKind.Meadows, "Clear", true, true, true, false));
            if (request is not null) { attempt = request; plan.Finish(request, request.RequestedTicks, request.RequestedTicks + 1, true); }
        }
        Assert(attempt is not null && attempt.ExclusionEndTicks.HasValue && plan.Attempts.Count == 1, "Stable-scene checkpoint or guard completion changed.");
        Assert(plan.ExcludesWindow(attempt!.ExclusionStartTicks, attempt.ExclusionStartTicks) && !plan.ExcludesWindow(attempt.ExclusionStartTicks - 2, attempt.ExclusionStartTicks - 1), "Census guard bounds changed.");
        plan.Stop(3200); Assert(plan.Missing.Count == 2, "Missing checkpoint reasons were lost.");

        string captureRoot = Path.Combine(output, "capture"); Directory.CreateDirectory(captureRoot);
        string frames = "UtcTimestamp,ElapsedMs,FrameIntervalMs,UnityUnscaledDeltaMs,Fps\n";
        DateTimeOffset start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        for (int i = 1; i <= 20; i++) frames += $"{start.AddMilliseconds(i * 100):O},{i * 100},100,100,10\n";
        File.WriteAllText(Path.Combine(captureRoot, "frames.csv"), frames);
        File.WriteAllText(Path.Combine(captureRoot, "recorder.json"), "{\"SupportsParticleCensus\":true}");
        const string header = "SnapshotId,StartSeconds,EndSeconds,StartFrame,EndFrame,Reason\n";
        string exclusions = Path.Combine(captureRoot, "particle-census-exclusions.csv");
        File.WriteAllText(exclusions, header + "1,0.5,0.8,4,8,particle_census_plus_guard_frames\n");
        Capture capture = CaptureReader.Read(captureRoot);
        capture.Methods.Add(new("census_method", .1, 1.5, 1000, 1, 1));
        capture.CpuSamples.Add(new(.6, 1, "census_leaf", "census_stack"));
        Analysis analysis = Analyzer.Analyze(capture, new AnalysisOptions(16, false, false));
        Assert(capture.Frames.Count == 20 && analysis.Included.Count < 20 && analysis.Included.All(frame => !frame.DiagnosticExcluded), "Census overhead was admitted or raw frames were discarded.");
        Assert(analysis.Findings.All(finding => !finding.Evidence.Contains("census_method", StringComparison.Ordinal))
            && analysis.Diagnostics.Issues.SelectMany(issue => issue.Methods).All(method => method.Name != "census_method"), "A guarded complete method window reached timing associations.");
        foreach (string name in new[] { "particle-census.csv", "particle-groups.csv" }) File.WriteAllText(Path.Combine(captureRoot, name), "Id,Name\n1,example\n");
        File.WriteAllText(Path.Combine(captureRoot, "particle-census-quality.json"), "{}");
        string export = Path.Combine(output, "report.zip"); ReportExporter.Export(analysis, export, includeRaw: false);
        using (var zip = ZipFile.OpenRead(export))
            foreach (string name in new[] { "particle-census.csv", "particle-groups.csv", "particle-census-exclusions.csv", "particle-census-quality.json" })
                Assert(zip.GetEntry("diagnostic-evidence/" + name) is not null, "Diagnostic export omitted " + name);
        File.WriteAllText(exclusions, header + "1,0.5,0.8,4,8,guard\n1,1,1.1,9,11,duplicate\n");
        Capture malformed = CaptureReader.Read(captureRoot);
        Assert(malformed.DiagnosticExclusionsInvalid && malformed.Frames.Count == 20 && Analyzer.Analyze(malformed, new AnalysisOptions()).Included.Count == 0, "Malformed diagnostic masks did not fail closed while retaining raw evidence.");
        File.WriteAllText(Path.Combine(captureRoot, "recorder.json"), "{\"SupportsCryptSnapshot\":true,\"CryptSnapshotMaskVersion\":\"2\"}");
        string crypt = Path.Combine(captureRoot, "crypt-snapshot-exclusions.csv");
        File.WriteAllText(crypt, header + "select-1,0.5,0.8,4,8,crypt_selection\narm-1,1,1.1,9,11,crypt_arm\nsnapshot,1.4,1.5,13,15,crypt_snapshot\n");
        Capture masked = CaptureReader.Read(captureRoot);
        Assert(!masked.DiagnosticExclusionsInvalid && masked.DiagnosticExclusions.Count == 3 && masked.Metadata.ContainsKey("DiagnosticExcludedCpuSamples"), "Preserved crypt selection/arm/snapshot mask support was lost.");
        Console.WriteLine("PASS " + (exactBaseline ? "exact historical recorder baseline" : "public generated metadata-version fixture") + ": IL capabilities, downgrade/same-revision/preimage protection, exact backup receipts, census plan/guards, import/analysis/export masks and crypt v2 exclusions.");

        GameTarget FixtureTarget(string name)
        {
            string profile = Path.Combine(output, name); Directory.CreateDirectory(Path.Combine(profile, "BepInEx", "core"));
            File.WriteAllText(Path.Combine(profile, "BepInEx", "core", "BepInEx.dll"), "fixture");
            return new(output, profile, "Isolated fixture");
        }
        string Bundle(string name, string recorder)
        {
            string path = Path.Combine(output, "bundle-" + name); Directory.CreateDirectory(Path.Combine(path, "Recorder"));
            File.Copy(recorder, Path.Combine(path, "Recorder", RecorderSetup.FileName)); return path;
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (IOException) { return; } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe recorder replacement was accepted.");
    }
}
