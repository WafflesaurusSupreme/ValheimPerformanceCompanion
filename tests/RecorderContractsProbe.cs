using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Diagnostics;
using System.Runtime.Serialization;

internal static class RecorderContractsProbe
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
        {
            string path = Path.Combine(args[1], new AssemblyName(eventArgs.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var assembly = Assembly.LoadFrom(args[0]);
        int failures = 0;
        foreach (string name in new[] { "Recording+Receipt", "Recording+Completion", "Recording+ConfigurationReceipt", "RecorderPlugin+StatusData" })
        {
            try
            {
                Type type = assembly.GetType("ValheimPerformanceCompanion.Recorder." + name, true);
                object value = Activator.CreateInstance(type);
                foreach (PropertyInfo property in type.GetProperties())
                {
                    object sample = property.PropertyType == typeof(string) ? "test [profile] 测试" :
                        property.PropertyType == typeof(int) ? (object)37 : property.PropertyType == typeof(long) ? (object)123456789L :
                        property.PropertyType == typeof(double) ? (object)17.25 : (object)true;
                    property.SetValue(value, sample, null);
                }
                var serializer = new DataContractJsonSerializer(type);
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, value);
                    string json = Encoding.UTF8.GetString(stream.ToArray());
                    stream.Position = 0;
                    object roundtrip = serializer.ReadObject(stream);
                    foreach (PropertyInfo property in type.GetProperties())
                    {
                        if (!json.Contains("\"" + property.Name + "\"") || !object.Equals(property.GetValue(value, null), property.GetValue(roundtrip, null)))
                            throw new Exception("Contract member lost: " + property.Name);
                    }
                }
                Console.WriteLine("PASS actual recorder contract on Valheim Mono: " + name);
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException()); }
        }
        try
        {
            Type type = assembly.GetType("ValheimPerformanceCompanion.Recorder.Recording", true);
            object recording = FormatterServices.GetUninitializedObject(type);
            string directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "clock-probe");
            Directory.CreateDirectory(directory);
            Action<string, object> set = (name, value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(recording, value);
            set("directory", directory);
            set("receipt", Activator.CreateInstance(assembly.GetType("ValheimPerformanceCompanion.Recorder.Recording+Receipt", true)));
            set("preparedAt", Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency);
            var elapsed = type.GetProperty("Elapsed");
            if ((double)elapsed.GetValue(recording, null) != 0) throw new Exception("Preparation consumed the measurement clock");
            var begin = type.GetMethod("Begin", BindingFlags.NonPublic | BindingFlags.Instance);
            begin.Invoke(recording, null);
            if ((double)elapsed.GetValue(recording, null) >= 5) throw new Exception("Start did not reset the measurement anchor");
            var anchor = type.GetField("start", BindingFlags.NonPublic | BindingFlags.Instance);
            object first = anchor.GetValue(recording);
            begin.Invoke(recording, null);
            if (!object.Equals(first, anchor.GetValue(recording))) throw new Exception("Duplicate Begin reset the clock");
            Console.WriteLine("PASS actual recorder clock on Valheim Mono: preparation excluded; Begin idempotent");
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL recorder clock: " + ex.GetBaseException()); }
        try
        {
            string folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "native-gc-probe");
            Directory.CreateDirectory(folder);
            Type native = assembly.GetType("ValheimPerformanceCompanion.Recorder.NativeGcTrace", true);
            object trace = Activator.CreateInstance(native, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { folder }, null);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string capability = (string)native.GetProperty("Capability", flags).GetValue(trace, null);
            if (!capability.StartsWith("Native Mono GC phase timestamps available", StringComparison.Ordinal)) throw new Exception(capability);
            native.GetMethod("Begin", flags).Invoke(trace, new object[] { Stopwatch.GetTimestamp() });
            for (int i = 0; i < 3; i++) { byte[] garbage = new byte[1024 * 1024]; GC.KeepAlive(garbage); GC.Collect(); }
            native.GetMethod("End", flags).Invoke(trace, null);
            string[] events = File.ReadAllLines(Path.Combine(folder, "gc-events.csv"));
            if (events.Length < 7 || !Array.Exists(events, line => line.Contains(",7,")) || !Array.Exists(events, line => line.Contains(",8,"))) throw new Exception("Native suspended-world phases missing");
            Console.WriteLine("PASS native GC callbacks on Valheim Mono: " + (events.Length - 1) + " phase events; managed callbacks not used");
            string secondFolder = Path.Combine(folder, "second"); Directory.CreateDirectory(secondFolder);
            object second = Activator.CreateInstance(native, flags, null, new object[] { secondFolder }, null);
            native.GetMethod("Begin", flags).Invoke(second, new object[] { Stopwatch.GetTimestamp() });
            GC.Collect(); native.GetMethod("End", flags).Invoke(second, null);
            string[] secondEvents = File.ReadAllLines(Path.Combine(secondFolder, "gc-events.csv"));
            if (secondEvents.Length < 3 || secondEvents.Length >= events.Length) throw new Exception("GC session buffer was not reset");
            Console.WriteLine("PASS repeated native GC capture resets events and reuses the pinned helper");
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL native GC timing: " + ex.GetBaseException()); }
        try
        {
            string folder = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "code-probe"); Directory.CreateDirectory(folder);
            Type inventory = assembly.GetType("ValheimPerformanceCompanion.Recorder.CodeInventory", true);
            inventory.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { folder, new[] { Assembly.GetExecutingAssembly() }, new System.Collections.Generic.Dictionary<Assembly, string>() });
            string code = File.ReadAllText(Path.Combine(folder, "code-methods.csv"));
            if (!code.Contains("RecorderContractsProbe") || !code.Contains("System.GC.Collect") || !code.Contains("IL_")) throw new Exception("Code snapshot missing method identity or resolved calls");
            if (!File.ReadAllText(Path.Combine(folder, "modules.csv")).Contains(Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString())) throw new Exception("Loaded module MVID missing");
            Console.WriteLine("PASS managed IL snapshot on Valheim Mono: module identity, original bytes and resolved GC call");
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL managed code snapshot: " + ex.GetBaseException()); }
        return failures == 0 ? 0 : 1;
    }
}
