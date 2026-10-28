using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class RecorderLateAttachProbe
{
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { System.Console.Error.WriteLine(error.GetBaseException()); return 1; }
    }
    private static async Task Run(string[] args)
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
        var shutdown = new CancellationTokenSource();
        foreach (string name in new[] { "requests", "consoleMessages" })
        {
            FieldInfo field = type.GetField(name, flags);
            field.SetValue(plugin, Activator.CreateInstance(field.FieldType));
        }
        type.GetField("shutdown", flags).SetValue(plugin, shutdown);
        int pid = Process.GetCurrentProcess().Id;
        type.GetField("pid", flags).SetValue(plugin, pid);
        // Seed the same immutable snapshot that Awake publishes before starting the real server.
        FieldInfo snapshot = type.GetField("statusJson", flags);
        if (snapshot != null) snapshot.SetValue(plugin, "{\"Protocol\":1,\"ProcessId\":" + pid + ",\"State\":\"Idle\",\"Error\":\"\"}");
        FieldInfo publishedAt = type.GetField("statusPublishedAt", flags);
        if (publishedAt != null) publishedAt.SetValue(plugin, Stopwatch.GetTimestamp());
        Task server = (Task)type.GetMethod("ServerLoop", flags).Invoke(plugin, null);
        try
        {
            // No Unity Update is executed: a minimized/stalled game must still answer and reconnect.
            foreach (string command in new[] { "hello", "status", "hello" })
            using (var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var timeout = new CancellationTokenSource(3000))
            {
                await pipe.ConnectAsync(1000, timeout.Token);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    await writer.WriteLineAsync(command);
                    Task<string> response = reader.ReadLineAsync();
                    if (await Task.WhenAny(response, Task.Delay(2000)) != response)
                        throw new TimeoutException("Recorder read-only request requires Unity Update and cannot answer while the game is stalled");
                    string json = await response;
                    if (json == null || !json.Contains("\"Idle\"") || !json.Contains(pid.ToString()))
                        throw new Exception("Read-only handshake/status failed without a Unity update: " + json);
                }
            }
            await Task.Delay(1100);
            using (var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(1000);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    await writer.WriteLineAsync("status");
                    Task<string> response = reader.ReadLineAsync();
                    if (await Task.WhenAny(response, Task.Delay(2000)) != response || !(await response).Contains("\"Idle\""))
                        throw new Exception("Older cached status was unavailable");
                }
            }
            using (var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(1000);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    await writer.WriteLineAsync("prepare\t" + Guid.NewGuid().ToString("N") + "\tMonitor\t5");
                    Task<string> response = reader.ReadLineAsync();
                    if (await Task.WhenAny(response, Task.Delay(400)) == response)
                        throw new Exception("A capture-changing command incorrectly received a cached response without the game thread");
                }
            }
            // Allow the abandoned mutation request's real server deadline to expire.
            await Task.Delay(12500);
            using (var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(2000);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    await writer.WriteLineAsync("status");
                    Task<string> response = reader.ReadLineAsync();
                    if (await Task.WhenAny(response, Task.Delay(2000)) != response || !(await response).Contains("awaiting the game thread"))
                        throw new Exception("An unreconciled capture command was incorrectly reported as cached Idle");
                }
            }
            System.Console.WriteLine("PASS actual recorder server on Valheim Mono answers hello/status and reconnects without Unity Update; cached reads remain available; capture mutations wait for the game thread and cannot return misleading Idle during recovery");
        }
        finally
        {
            shutdown.Cancel();
            if (await Task.WhenAny(server, Task.Delay(3000)) != server) throw new Exception("Recorder server did not stop");
            await server;
            shutdown.Dispose();
        }
    }
}
