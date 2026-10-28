using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Companion.App;
using Companion.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XDocument styles = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppStyles.xaml"));
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            styles.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        app.Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            try
            {
                string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                string profile = Path.Combine(root, "Profile [test] 测试");
                if (args.Length > 1 && args[1] == "--history")
                {
                    string captures = Path.Combine(profile, "BepInEx", "PerformanceCompanion", "Captures");
                    string baseline = Path.Combine(captures, "first", "frames.csv");
                    string other = Path.Combine(captures, "second", "frames.csv");
                    Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(other)!);
                    const string frames = "UtcTimestamp,ElapsedMs,FrameIntervalMs,UnityUnscaledDeltaMs,Fps\n"
                        + "2026-09-27T12:00:00Z,0,0,0,0\n2026-09-27T12:00:00.010Z,10,10,10,100\n"
                        + "2026-09-27T12:00:00.040Z,40,30,30,33.3333\n";
                    File.WriteAllText(baseline, frames); File.WriteAllText(other, frames);
                    var history = new CaptureHistory(Path.Combine(root, "settings", "recent-captures.json"));
                    var discovered = history.Read(profile);
                    if (discovered.Recent.Count != 2 || discovered.LastOpened is not null) throw new Exception("Existing profile captures were not discovered.");
                    history.Remember(baseline);
                    var options = new AnalysisOptions(20, false, true, 0, double.MaxValue);
                    history.RememberComparison(baseline, other, options);
                    var restored = new CaptureHistory(Path.Combine(root, "settings", "recent-captures.json")).Read(profile);
                    if (restored.LastOpened != baseline || restored.Recent.Count != 2 || restored.Comparisons.Count != 1
                        || restored.Comparisons[0].OtherPath != other || restored.Comparisons[0].Options != options)
                        throw new Exception("Results and comparison history did not survive a new store instance.");
                    window = new MainWindow(true, history);
                    const BindingFlags historyFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(MainWindow).GetField("preferredTarget", historyFlags)!.SetValue(window, null);
                    window.ShowInTaskbar = false; window.Left = -20000; window.Show();
                    await (Task)typeof(MainWindow).GetMethod("RestoreHistoryAsync", historyFlags)!.Invoke(window, null)!;
                    if (((TextBlock)window.FindName("CaptureTitle")).Text != "first"
                        || string.IsNullOrWhiteSpace(((TextBlock)window.FindName("Comparison")).Text)
                        || ((TextBox)window.FindName("Budget")).Text != "20")
                        throw new Exception("Results and comparison were not restored in the WPF window.");
                    var analysis = (Analysis)typeof(MainWindow).GetField("analysis", historyFlags)!.GetValue(window)!;
                    var saveDialog = (Microsoft.Win32.SaveFileDialog)typeof(MainWindow).GetMethod("ReportDialog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
                    if (saveDialog.FilterIndex != 1 || !saveDialog.Filter.Contains("Full diagnostics ZIP with raw CPU trace", StringComparison.Ordinal)
                        || !saveDialog.Filter.Contains("Redacted report ZIP", StringComparison.Ordinal))
                        throw new Exception("Save As must default to a full diagnostics ZIP with a separate redacted choice.");
                    var openButton = (Button)window.FindName("OpenCaptureButton");
                    var exportButton = (Button)window.FindName("ExportReportButton");
                    typeof(MainWindow).GetField("busy", historyFlags)!.SetValue(window, true);
                    typeof(MainWindow).GetMethod("UpdateControls", historyFlags)!.Invoke(window, null);
                    if (openButton.IsEnabled || exportButton.IsEnabled) throw new Exception("Capture dialogs must be unavailable during an active operation.");
                    typeof(MainWindow).GetField("busy", historyFlags)!.SetValue(window, false);
                    typeof(MainWindow).GetMethod("UpdateControls", historyFlags)!.Invoke(window, null);
                    if (!openButton.IsEnabled || !exportButton.IsEnabled) throw new Exception("Capture dialogs did not become available after the operation.");
                    string report = Path.Combine(root, "report.zip");
                    await (Task)typeof(MainWindow).GetMethod("ExportSelectedReportAsync", historyFlags)!.Invoke(window, [report, true])!;
                    using (var zip = ZipFile.OpenRead(report))
                        if (!zip.Entries.Any(entry => entry.FullName == "report.html") || !zip.Entries.Any(entry => entry.FullName == "raw/frames.csv"))
                            throw new Exception("Full diagnostics ZIP is missing its report or raw capture.");
                    window.Close();
                    Console.WriteLine("PASS history restore, header busy state, Save As defaults, and full diagnostics ZIP.");
                    app.Shutdown(0); return;
                }
                Directory.CreateDirectory(Path.Combine(profile, "BepInEx", "core"));
                File.WriteAllText(Path.Combine(profile, "BepInEx", "core", "BepInEx.dll"), "test fixture");
                var target = new GameTarget(root, profile, "test fixture");
                // The preview flag prevents discovery from installing into any real profile.
                window = new MainWindow(true);
                if (args.Length > 1 && args[1] == "--close-only")
                {
                    var closed = new TaskCompletionSource();
                    app.DispatcherUnhandledException += (_, e) => { e.Handled = true; closed.TrySetException(e.Exception); };
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000;
                    window.Show(); window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Console.WriteLine("PASS: real WPF window closes without reentering its Closing event.");
                    app.Shutdown(0); return;
                }
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var prepare = typeof(MainWindow).GetMethod("PrepareTargetAsync", flags)!;
                if (args.Length > 1 && args[1] == "--late-attach")
                {
                    string recorderPath = Path.Combine(target.BepInExRoot, "plugins", "ValheimPerformanceCompanion", RecorderSetup.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(recorderPath)!);
                    File.WriteAllText(recorderPath, "an older recorder already loaded by the game");
                    foreach (bool unknownProfile in new[] { true, false })
                    {
                        var running = new GameTarget(root, unknownProfile ? null : profile, "already running", Environment.ProcessId);
                        using var attachPipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + Environment.ProcessId,
                            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                        var reply = Task.Run(async () =>
                        {
                            await attachPipe.WaitForConnectionAsync(timeout.Token);
                            using var reader = new StreamReader(attachPipe, Encoding.UTF8, false, 1024, true);
                            using var writer = new StreamWriter(attachPipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                            if (await RecorderClient.ReadBoundedAsync(reader, 1024, timeout.Token) != "hello") throw new Exception("Expected late-attach handshake");
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderStatus { Protocol = 1, ProcessId = Environment.ProcessId,
                                GameDirectory = root, BepInExRoot = target.BepInExRoot, State = "Idle", SupportsPreparation = true }));
                        });
                        await (Task)prepare.Invoke(window, [running])!;
                        await reply;
                        if (!((Button)window.FindName("StartButton")).IsEnabled
                            || !((TextBlock)window.FindName("StatusText")).Text.StartsWith("Connected", StringComparison.Ordinal))
                            throw new Exception("An already-running recorder was blocked by disk setup or unknown profile");
                        if (File.ReadAllText(recorderPath) != "an older recorder already loaded by the game")
                            throw new Exception("Late attachment rewrote the recorder in the running profile");
                    }
                    await ((CaptureController)typeof(MainWindow).GetField("controller", flags)!.GetValue(window)!).DisposeAsync();
                    Console.WriteLine("PASS late attachment verifies the live profile before disk setup, accepts a compatible loaded recorder and leaves its files unchanged");
                    app.Shutdown(0); return;
                }
                string installed = Path.Combine(target.BepInExRoot, "plugins", "ValheimPerformanceCompanion", RecorderSetup.FileName);
                // Preinstall only the isolated fixture when the player's game is running.
                if (RecorderSetup.GameRunning())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Recorder", RecorderSetup.FileName), installed, true);
                }
                if (args.Length > 1 && args[1] == "--retry")
                {
                    var slow = target with { ProcessId = Environment.ProcessId };
                    typeof(MainWindow).GetField("preferredTarget", flags)!.SetValue(window, slow);
                    await (Task)prepare.Invoke(window, [slow])!;
                    var retry = typeof(MainWindow).GetField("retryConnectionAt", flags)!;
                    var tick = typeof(MainWindow).GetMethod("TickAsync", flags)!;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        retry.SetValue(window, DateTimeOffset.UtcNow.AddSeconds(-1));
                        await (Task)tick.Invoke(window, null)!;
                        if (retry.GetValue(window) is null) throw new Exception("Recorder retry stopped during slow game startup");
                        if (((TextBlock)window.FindName("StatusText")).Text.Contains("Restart", StringComparison.OrdinalIgnoreCase))
                            throw new Exception("Missing response incorrectly diagnosed as requiring a restart");
                    }
                    Console.WriteLine("PASS recorder retry continues beyond the old three-attempt cutoff without blaming the launch method");
                    app.Shutdown(0); return;
                }
                await (Task)prepare.Invoke(window, [target])!;
                var timer = (DispatcherTimer)typeof(MainWindow).GetField("discoveryTimer", flags)!.GetValue(window)!;
                if (!timer.IsEnabled || timer.Interval != TimeSpan.FromSeconds(30)) throw new Exception("PID discovery timer not enabled at 30 seconds.");
                if (!RecorderSetup.GameRunning() && !File.Exists(installed)) throw new Exception("Automatic setup did not install into fixture profile.");
                if (((Panel)window.Content).Children.Count == 0) throw new Exception("Window content missing.");
                var fired = new TaskCompletionSource();
                timer.Tick += (_, _) => fired.TrySetResult();
                var elapsed = Stopwatch.StartNew();
                await fired.Task.WaitAsync(TimeSpan.FromSeconds(40));
                timer.Stop();
                if (elapsed.Elapsed.TotalSeconds < 29) throw new Exception("Timer fired earlier than 30 seconds.");
                // Tick starts an asynchronous discovery operation. Respect the same busy gate as the UI.
                var idleDeadline = Stopwatch.StartNew();
                while ((bool)typeof(MainWindow).GetField("busy", flags)!.GetValue(window)!)
                {
                    if (idleDeadline.Elapsed.TotalSeconds > 10) throw new Exception("Background discovery did not finish");
                    await Task.Delay(50);
                }
                // Exercise the actual UI/controller handshake after discovering a PID.
                var found = target with { ProcessId = Environment.ProcessId };
                using var pipe = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + Environment.ProcessId,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var serve = Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await pipe.WaitForConnectionAsync(timeout.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    if (await RecorderClient.ReadBoundedAsync(reader, 1024, timeout.Token) != "hello") throw new Exception("Expected handshake");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderStatus { Protocol = 1, ProcessId = Environment.ProcessId,
                        GameDirectory = root, BepInExRoot = target.BepInExRoot, State = "Idle" }));
                });
                await (Task)prepare.Invoke(window, [found])!;
                await serve;
                if (timer.IsEnabled) throw new Exception("PID search continued after finding a PID.");
                if (!((TextBlock)window.FindName("StatusText")).Text.StartsWith("Connected", StringComparison.Ordinal)
                    || !((Button)window.FindName("StartButton")).IsEnabled) throw new Exception("Connected status/capture control not enabled after handshake: " + ((TextBlock)window.FindName("StatusText")).Text);
                if (args.Length > 1 && args[1] == "--disconnect")
                {
                    pipe.Dispose();
                    await (Task)typeof(MainWindow).GetMethod("PollAsync", flags)!.Invoke(window, null)!;
                    if ((bool)typeof(MainWindow).GetField("connected", flags)!.GetValue(window)!
                        || typeof(MainWindow).GetField("retryConnectionAt", flags)!.GetValue(window) is null || timer.IsEnabled)
                        throw new Exception("Poll failure did not schedule recorder reconnection");
                    using var reconnect = new NamedPipeServerStream("ValheimPerformanceCompanion.Recorder." + Environment.ProcessId,
                        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    var reply = Task.Run(async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await reconnect.WaitForConnectionAsync(timeout.Token);
                        using var reader = new StreamReader(reconnect, Encoding.UTF8, false, 1024, true);
                        using var writer = new StreamWriter(reconnect, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                        if (await RecorderClient.ReadBoundedAsync(reader, 1024, timeout.Token) != "hello") throw new Exception("Expected reconnect hello");
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new RecorderStatus { Protocol = 1, ProcessId = Environment.ProcessId,
                            GameDirectory = root, BepInExRoot = target.BepInExRoot, State = "Idle" }));
                    });
                    typeof(MainWindow).GetField("retryConnectionAt", flags)!.SetValue(window, DateTimeOffset.UtcNow.AddSeconds(-1));
                    await (Task)typeof(MainWindow).GetMethod("TickAsync", flags)!.Invoke(window, null)!;
                    await reply;
                    if (!(bool)typeof(MainWindow).GetField("connected", flags)!.GetValue(window)! || !((Button)window.FindName("StartButton")).IsEnabled)
                        throw new Exception("Recorder did not reconnect automatically after a polling failure");
                    Console.WriteLine("PASS polling failure schedules retry, reconnects and enables capture");
                }
                var finished = new TaskCompletionSource();
                window.Closed += (_, _) => finished.TrySetResult();
                window.ShowInTaskbar = false; window.Left = -20000; window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Show(); window.Close(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                File.WriteAllText(Path.Combine(root, "wpf-verification.txt"), $"PASS: production preparation path, bundle-relative recorder, real DispatcherTimer tick at {elapsed.Elapsed.TotalSeconds:F2}s, search stops on PID, handshake changes status to Connected and enables capture, connected-window close. No real profile changed.");
                Console.WriteLine(File.ReadAllText(Path.Combine(root, "wpf-verification.txt")));
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; app.Shutdown(1); }
        });
        app.Run();
    }
}
