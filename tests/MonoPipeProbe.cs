using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Companion.Transport;

public sealed class ProbeStatus
{
    public int Protocol { get; set; }
    public int ProcessId { get; set; }
    public string State { get; set; }
    public string Error { get; set; }
}
internal static class MonoPipeProbe
{
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { System.Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Run(string[] args)
    {
        int pid = Process.GetCurrentProcess().Id;
        using (var timeout = new CancellationTokenSource(20000))
        {
            // Actual production transport on Valheim's Mono, read by the production .NET client.
            for (int attempt = 0; attempt < 2; attempt++)
            using (var pipe = WindowsPipe.CreateServer("ValheimPerformanceCompanion.Recorder." + pid))
            {
                File.WriteAllText(args[0], pid.ToString());
                await pipe.WaitForConnectionAsync(timeout.Token);
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                {
                    if (await reader.ReadLineAsync() != "hello") throw new Exception("Expected hello");
                    using (var data = new MemoryStream())
                    {
                        new DataContractJsonSerializer(typeof(ProbeStatus)).WriteObject(data, new ProbeStatus { Protocol = 1, ProcessId = pid, State = "Idle", Error = "" });
                        await writer.WriteLineAsync(Encoding.UTF8.GetString(data.ToArray()));
                    }
                }
            }
            // Actual console-side client options and user verification, against .NET's controller pipe.
            using (var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Controller." + pid, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(10000, timeout.Token);
                WindowsPipe.VerifyServer(pipe);
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    await writer.WriteLineAsync("status");
                    if (await reader.ReadLineAsync() != "Idle") throw new Exception("Console reply mismatch");
                }
            }
            using (var pipe = WindowsPipe.CreateServer("CompanionCancellation." + pid))
            using (var cancel = new CancellationTokenSource(100))
            {
                try { await pipe.WaitForConnectionAsync(cancel.Token); throw new Exception("Wait did not cancel"); }
                catch (OperationCanceledException) { }
            }
        }
        System.Console.WriteLine("PASS Valheim Mono server, desktop handshake, JSON, console client and cancellation");
    }
}
