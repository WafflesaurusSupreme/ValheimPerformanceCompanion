using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string path = Path.Combine(args[1], new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Assembly recorder = Assembly.LoadFrom(args[0]);
            Check(recorder.GetName().Version == new Version(0, 1, 10, 0), "Recorder version mismatch");
            Type type = recorder.GetType("ValheimPerformanceCompanion.Recorder.NativeGcTrace", true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo begin = type.GetMethod("Begin", flags)!, end = type.GetMethod("End", flags)!;
            var map = (Func<long, long, long, long, long>)Delegate.CreateDelegate(typeof(Func<long, long, long, long, long>), type.GetMethod("MapQpcAnchor", BindingFlags.NonPublic | BindingFlags.Static)!);
            Check(map(100, 200, 202, 100000) == 99899, "Midpoint mapping wrong");
            foreach (long[] sample in new[] { new long[] { -1, 1, 2, 3 }, new long[] { 1, 5, 4, 3 }, new long[] { 5, 1, 2, 3 }, new long[] { 1, -1, 2, 3 }, new long[] { 1, 1, 2, -1 }, new long[] { 1, 1, Stopwatch.Frequency, 3 } })
            {
                bool rejected = false;
                try { map(sample[0], sample[1], sample[2], sample[3]); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Invalid clock observation accepted");
            }
            int events = 0;
            for (int capture = 0; capture < 3; capture++)
            {
                string directory = Path.Combine(args[2], "capture-" + capture); Directory.CreateDirectory(directory);
                object trace = Activator.CreateInstance(type, flags, null, new object[] { directory }, CultureInfo.InvariantCulture)!;
                long start = Stopwatch.GetTimestamp(); begin.Invoke(trace, new object[] { start });
                long mapped = (long)type.GetField("anchor", flags)!.GetValue(trace);
                Check(mapped > start + Stopwatch.Frequency, "Native QPC origin not corrected");
                Thread.Sleep(20); GC.Collect(); Thread.Sleep(10); end.Invoke(trace, null);
                double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                string csv = Path.Combine(directory, "gc-events.csv"); Check(File.Exists(csv), "Native GC probe failed to start");
                foreach (string line in File.ReadAllLines(csv).Skip(1))
                {
                    double seconds = double.Parse(line.Split(',')[0], CultureInfo.InvariantCulture);
                    Check(seconds >= -0.001 && seconds <= elapsed + 0.002, "GC timestamp outside capture"); events++;
                }
            }
            Check(events > 0, "No native GC events observed");
            string invalidDirectory = Path.Combine(args[2], "invalid-anchor"); Directory.CreateDirectory(invalidDirectory);
            object invalid = Activator.CreateInstance(type, flags, null, new object[] { invalidDirectory }, CultureInfo.InvariantCulture)!;
            begin.Invoke(invalid, new object[] { -1L }); GC.Collect(); end.Invoke(invalid, null);
            Check(!(bool)type.GetField("running", flags)!.GetValue(invalid), "Invalid clock started timing");
            Check(!File.Exists(Path.Combine(invalidDirectory, "gc-events.csv")), "Invalid clock exported timestamps");
            Check(File.ReadAllText(Path.Combine(invalidDirectory, "gc-quality.txt")).Contains("alignment unavailable"), "Invalid clock quality warning missing");
            Console.WriteLine("COMPANION_CLOCK_MONO=PASS version=0.1.10 captures=3 events=" + events + " within capture bounds; invalid observations rejected, bad-anchor capture disabled with quality warning; no PerformancePack/Harmony clock dependency.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
