using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Companion.Core;

string ready = Path.GetFullPath(args[3]);
var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(Path.GetFullPath(args[1]));
start.ArgumentList.Add(Path.GetFullPath(args[2]));
start.ArgumentList.Add(Path.GetFullPath(args[4]));
start.ArgumentList.Add(ready);
using var process = Process.Start(start) ?? throw new Exception("Mono host did not start");
Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (!File.Exists(ready) && !process.HasExited) await Task.Delay(50, timeout.Token);
    if (!File.Exists(ready)) throw new Exception("Mono server failed: " + await errors);
    int pid = int.Parse(await File.ReadAllTextAsync(ready, timeout.Token));
    if (pid != process.Id) throw new Exception("Server PID mismatch");
    using var controller = new NamedPipeServerStream("ValheimPerformanceCompanion.Controller." + pid, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    // Inspect the real Mono-created Windows ACL before sending the handshake.
    using (var inspect = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        await inspect.ConnectAsync(2000, timeout.Token);
        PipeSecurity security = inspect.GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || rules.Length != 1 || rules[0].AccessControlType != AccessControlType.Allow
            || !rules[0].IdentityReference.Equals(WindowsIdentity.GetCurrent().User)) throw new Exception("Pipe ACL is not restricted to the current user");
        using var writer = new StreamWriter(inspect, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        using var reader = new StreamReader(inspect, Encoding.UTF8, false, 1024, true);
        await writer.WriteLineAsync("hello");
        string line = await RecorderClient.ReadBoundedAsync(reader, 16384, timeout.Token);
        var response = System.Text.Json.JsonSerializer.Deserialize<RecorderStatus>(line)!;
        if (response.ProcessId != pid || response.State != "Idle") throw new Exception("Mono JSON mismatch");
    }
    RecorderStatus hello = await RecorderClient.SendAsync(pid, "hello", timeout.Token);
    if (hello.State != "Idle") throw new Exception("Production recorder client handshake failed");
    await controller.WaitForConnectionAsync(timeout.Token);
    using var consoleReader = new StreamReader(controller, Encoding.UTF8, false, 1024, true);
    using var consoleWriter = new StreamWriter(controller, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
    if (await RecorderClient.ReadBoundedAsync(consoleReader, 1024, timeout.Token) != "status") throw new Exception("Console command mismatch");
    await consoleWriter.WriteLineAsync("Idle");
    await process.WaitForExitAsync(timeout.Token);
    Console.Write(await output); Console.Error.Write(await errors);
    if (process.ExitCode != 0) throw new Exception("Mono probe failed");
    Console.WriteLine("PASS current-user-only ACL and bidirectional Mono/.NET exchange");
}
finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
