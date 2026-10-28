using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;

internal static class RecorderIdleProbe
{
    private static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string path = Path.Combine(args[1], new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Assembly assembly = Assembly.LoadFrom(args[0]);
            Type type = assembly.GetType("ValheimPerformanceCompanion.Recorder.RecorderPlugin", true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object plugin = FormatterServices.GetUninitializedObject(type);
            foreach (string name in new[] { "requests", "consoleMessages" })
            {
                FieldInfo field = type.GetField(name, flags);
                field.SetValue(plugin, Activator.CreateInstance(field.FieldType));
            }
            var update = (Action)Delegate.CreateDelegate(typeof(Action), plugin, type.GetMethod("Update", flags));
            MethodInfo allocatedMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
            var allocated = allocatedMethod == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocatedMethod);
            const int iterations = 1000000;
            for (int i = 0; i < 10000; i++) update();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var watch = new Stopwatch();
            long before = allocated == null ? 0 : allocated();
            int collections = GC.CollectionCount(0);
            watch.Start();
            for (int i = 0; i < iterations; i++) update();
            watch.Stop();
            long bytes = allocated == null ? -1 : allocated() - before;
            System.Console.WriteLine("RECORDER_IDLE version=" + assembly.GetName().Version + " iterations=" + iterations
                + " elapsedMs=" + watch.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " allocatedBytes=" + bytes + " gen0Collections=" + (GC.CollectionCount(0) - collections));
            return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error.GetBaseException()); return 1; }
    }
}
