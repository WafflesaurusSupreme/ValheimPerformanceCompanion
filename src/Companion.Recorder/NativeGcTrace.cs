using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ValheimPerformanceCompanion.Recorder;

internal sealed class NativeGcTrace
{
    [StructLayout(LayoutKind.Sequential)] private struct Event { public long Ticks; public uint Thread; public int Phase; public uint Generation; public int Serial; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Command();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Count();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Read(uint index, out Event value);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long value);
    private static Command? begin, end;
    private static Count? count;
    private static Read? read;
    private readonly string directory;
    private long anchor;
    private bool running;
    internal string Capability { get; private set; } = "Unavailable";
    internal NativeGcTrace(string directory)
    {
        this.directory = directory;
        try
        {
            if (begin == null)
            {
                using Stream resource = typeof(NativeGcTrace).Assembly.GetManifestResourceStream("VpcGcProbe.dll") ?? throw new IOException("Native GC resource missing");
                using var memory = new MemoryStream(); resource.CopyTo(memory); byte[] bytes = memory.ToArray();
                using var sha = SHA256.Create(); string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                string root = Path.Combine(Path.GetDirectoryName(directory)!, "native", hash);
                Directory.CreateDirectory(root); string path = Path.Combine(root, "VpcGcProbe.dll");
                if (!File.Exists(path)) { using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes, 0, bytes.Length); }
                using (var input = File.OpenRead(path))
                    if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") != hash) throw new IOException("Native GC helper hash mismatch");
                IntPtr library = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
                if (library == IntPtr.Zero) throw new IOException("Native GC helper load failed: " + Marshal.GetLastWin32Error());
                T Function<T>(string name) where T : Delegate
                { IntPtr address = GetProcAddress(library, name); if (address == IntPtr.Zero) throw new IOException("Missing export " + name); return Marshal.GetDelegateForFunctionPointer<T>(address); }
                if (Function<Command>("vpc_gc_init")() != 1) throw new IOException("Mono GC callback API unavailable");
                end = Function<Command>("vpc_gc_end"); count = Function<Count>("vpc_gc_count"); read = Function<Read>("vpc_gc_read"); begin = Function<Command>("vpc_gc_begin");
            }
            Capability = "Native Mono GC phase timestamps available; allocation call stacks unavailable (startup instrumentation required).";
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is BadImageFormatException || error is NotSupportedException)
        { Capability = "Native GC timing unavailable: " + error.Message; }
        File.WriteAllText(Path.Combine(directory, "gc-capability.txt"), Capability, new UTF8Encoding(false));
    }
    internal void Begin(long start)
    {
        running = false;
        if (begin != null)
        {
            try
            {
                if (!QueryPerformanceFrequency(out long frequency) || frequency != Stopwatch.Frequency)
                    throw new InvalidOperationException("Native GC and recorder clock frequencies differ.");
                long before = Stopwatch.GetTimestamp();
                if (!QueryPerformanceCounter(out long qpc)) throw new InvalidOperationException("Native GC clock unavailable.");
                long after = Stopwatch.GetTimestamp();
                anchor = MapQpcAnchor(start, before, after, qpc);
                running = begin() == 1;
            }
            catch (Exception error) when (error is InvalidOperationException || error is OverflowException)
            {
                File.WriteAllText(Path.Combine(directory, "gc-quality.txt"), "Native GC clock alignment unavailable; phase timestamps were not collected: " + error.Message, new UTF8Encoding(false));
                return;
            }
        }
        if (!running) File.WriteAllText(Path.Combine(directory, "gc-quality.txt"), "Native GC collection did not start; pause timing is unavailable.", new UTF8Encoding(false));
    }
    internal static long MapQpcAnchor(long start, long before, long after, long qpc)
    {
        if (start < 0 || before < 0 || qpc < 0 || after < before || start > after || after - before > Stopwatch.Frequency / 1000)
            throw new InvalidOperationException("Clock observation is invalid or exceeds one millisecond uncertainty.");
        long midpoint = checked(before + (after - before) / 2);
        return checked(qpc - (midpoint - start));
    }
    internal void End()
    {
        if (!running) return;
        var timeout = Stopwatch.StartNew();
        while (end!() != 1) { if (timeout.ElapsedMilliseconds > 1000) throw new IOException("Native GC callback did not quiesce"); Thread.Sleep(1); }
        running = false;
        using var writer = new StreamWriter(Path.Combine(directory, "gc-events.csv"), false, new UTF8Encoding(false));
        writer.WriteLine("Seconds,ThreadId,Phase,Generation,Serial");
        uint total = count!();
        for (uint i = 0; i < Math.Min(total, 65536); i++)
        {
            if (read!(i, out Event value) != 1) throw new IOException("Native GC event unavailable at " + i);
            writer.WriteLine(((value.Ticks - anchor) / (double)Stopwatch.Frequency).ToString("F9", CultureInfo.InvariantCulture) + "," + value.Thread + "," + value.Phase + "," + value.Generation + "," + value.Serial);
        }
        if (total > 65536) File.WriteAllText(Path.Combine(directory, "gc-quality.txt"), "Native GC buffer overflow: " + (total - 65536) + " events omitted; pause coverage incomplete.", new UTF8Encoding(false));
    }
}
