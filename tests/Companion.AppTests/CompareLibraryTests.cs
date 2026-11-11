using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Companion.App;
using Companion.Core;

internal static class CompareLibraryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Frames = "UtcTimestamp,ElapsedMs,FrameIntervalMs,UnityUnscaledDeltaMs,Fps\n"
        + "2026-09-27T12:00:00Z,0,0,0,0\n2026-09-27T12:00:00.010Z,10,10,10,100\n"
        + "2026-09-27T12:00:00.040Z,40,30,30,33.3333\n";

    public static async Task RunAsync(string root)
    {
        string profile = Path.Combine(root, "-Profile [test] Ω 测试's $notes");
        string captures = Path.Combine(profile, "BepInEx", "PerformanceCompanion", "Captures");
        string baseline = WriteCapture("first", "Swamp route · rain", "2026-09-27T12:00:00Z");
        string other = WriteCapture("second", "Swamp route · clear", "2026-09-28T12:00:00Z");
        for (int i = 0; i < 31; i++) WriteCapture("older-" + i, "", "2026-09-20T12:00:00Z");
        string legacy = Path.Combine(captures, "frames-legacy.csv");
        File.WriteAllText(legacy, Frames);
        string bad = WriteCapture("bad-metadata", "", "2026-09-20T12:00:00Z");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(bad)!, "session.json"), "{");
        File.SetLastWriteTimeUtc(baseline, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var history = new CaptureHistory(Path.Combine(root, "settings", "recent-captures.json"));
        var discovered = history.Read(profile);
        if (discovered.Recent.Count != 35 || discovered.Comparisons.Count != 0)
            throw new Exception("The library must discover all default-folder recordings before any saved pair exists, including root-level legacy CSVs.");
        if (string.IsNullOrWhiteSpace(discovered.Recent.Single(row => row.Path == bad).MetadataWarning))
            throw new Exception("A recording with unreadable optional metadata must remain visible with a diagnostic.");
        string corruptState = Path.Combine(root, "corrupt-history.json");
        File.WriteAllText(corruptState, "{");
        var recovered = new CaptureHistory(corruptState).ReadLibrary(profile);
        if (recovered.Recent.Count != 35 || recovered.Warnings.Count == 0 || File.ReadAllText(corruptState) != "{")
            throw new Exception("Unreadable history must not hide profile recordings or overwrite the damaged state.");

        string emptyProfile = Path.Combine(root, "empty-profile");
        string emptyCaptures = Path.Combine(emptyProfile, "BepInEx", "PerformanceCompanion", "Captures");
        Directory.CreateDirectory(emptyCaptures);
        using (var emptyScope = new WindowScope(new MainWindow(true, new CaptureHistory(Path.Combine(root, "empty-history.json")))))
        {
            typeof(MainWindow).GetField("preferredTarget", Private)!.SetValue(emptyScope.Window, new GameTarget(root, emptyProfile, "Empty test"));
            ((TabControl)emptyScope.Window.FindName("Tabs")).SelectedIndex = 2;
            var emptyDialog = (Microsoft.Win32.OpenFileDialog)typeof(MainWindow).GetMethod("CaptureDialog", Private)!.Invoke(emptyScope.Window, null)!;
            if (emptyDialog.InitialDirectory != emptyCaptures || ((Button)emptyScope.Window.FindName("OpenSelectedCaptureButton")).IsEnabled
                || ((Button)emptyScope.Window.FindName("ChooseComparisonButton")).IsEnabled)
                throw new Exception("An empty profile must use its capture root as the picker fallback and disable actions requiring a recording.");
        }

        using var windowScope = new WindowScope(new MainWindow(true, history));
        MainWindow window = windowScope.Window;
        typeof(MainWindow).GetField("preferredTarget", Private)!.SetValue(window, new GameTarget(root, profile, "Isolated test"));
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Show();
        var tabs = (TabControl)window.FindName("Tabs");
        tabs.SelectedIndex = 2;
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var library = (DataGrid?)window.FindName("ComparisonCaptures")
            ?? throw new Exception("Compare must display a recording table, not an empty saved-pair selector.");
        if (library.Items.Count != 35) throw new Exception("Opening Compare did not refresh the default capture library.");
        object baselineRow = library.Items.Cast<object>().Single(row => Property<string>(row, "Path") == baseline);
        if (Property<string>(baselineRow, "Scenario") != "Swamp route · rain"
            || Property<DateTime>(baselineRow, "StartedUtc") != new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc))
            throw new Exception("The table must show recorded scenario notes and capture time, independent of file modification time.");
        object legacyRow = library.Items.Cast<object>().Single(row => Property<string>(row, "Path") == legacy);
        if (Property<DateTime>(legacyRow, "StartedUtc") != new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc))
            throw new Exception("Legacy recordings must obtain timestamps from their frames.");
        if (((Button)window.FindName("OpenSavedComparisonButton")).IsEnabled)
            throw new Exception("Open previous comparison must be unavailable when there are no saved pairs.");
        AssertDialog(Path.GetDirectoryName(other)!);
        library.SelectedItem = baselineRow;
        await ClickAsync("OpenSelectedCaptureButton");
        if (((TextBlock)window.FindName("CaptureTitle")).Text != "first") throw new Exception("The table could not open a baseline recording.");
        tabs.SelectedIndex = 2;
        library.SelectedItem = library.Items.Cast<object>().Single(row => Property<string>(row, "Path") == other);
        await ClickAsync("CompareSelectedCaptureButton");
        if (string.IsNullOrWhiteSpace(((TextBlock)window.FindName("Comparison")).Text) || tabs.SelectedIndex != 2)
            throw new Exception("Comparing a listed recording must show the comparison on Compare.");
        if (history.Read(profile).Comparisons.Count != 1 || !((Button)window.FindName("OpenSavedComparisonButton")).IsEnabled)
            throw new Exception("A table comparison was not saved and made available to reopen.");
        await window.LoadCaptureAsync(other);
        await ClickAsync("OpenSavedComparisonButton");
        if (((TextBlock)window.FindName("CaptureTitle")).Text != "first"
            || string.IsNullOrWhiteSpace(((TextBlock)window.FindName("Comparison")).Text) || tabs.SelectedIndex != 2)
            throw new Exception("Open previous comparison did not restore its baseline and comparison.");
        library.SelectedItem = library.Items.Cast<object>().Single(row => Property<string>(row, "Path") == baseline);
        if (((Button)window.FindName("CompareSelectedCaptureButton")).IsEnabled)
            throw new Exception("A recording cannot be compared against itself.");
        typeof(MainWindow).GetField("busy", Private)!.SetValue(window, true);
        typeof(MainWindow).GetMethod("UpdateControls", Private)!.Invoke(window, null);
        if (((Button)window.FindName("OpenSelectedCaptureButton")).IsEnabled
            || ((Button)window.FindName("OpenSavedComparisonButton")).IsEnabled)
            throw new Exception("Library actions must respect the current busy operation.");
        typeof(MainWindow).GetField("busy", Private)!.SetValue(window, false);
        typeof(MainWindow).GetMethod("UpdateControls", Private)!.Invoke(window, null);
        typeof(MainWindow).GetField("preferredTarget", Private)!.SetValue(window, null);
        AssertDialog(Path.GetDirectoryName(baseline)!);
        typeof(MainWindow).GetField("preferredTarget", Private)!.SetValue(window, new GameTarget(profile, null, "Direct BepInEx test"));
        AssertDialog(Path.GetDirectoryName(baseline)!);
        tabs.SelectedIndex = 0;
        WriteCapture("new-capture", "New recording", "2026-09-29T12:00:00Z");
        tabs.SelectedIndex = 2;
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (library.Items.Count != 36) throw new Exception("Returning to Compare must find newly written captures.");
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var image = File.Create(Path.Combine(root, "compare-page.png"))) encoder.Save(image);
        Console.WriteLine("PASS Compare library discovery, timestamps/notes, baseline/compare/reopen buttons, busy state, refresh and capture-dialog directories.");

        string WriteCapture(string name, string notes, string start)
        {
            string directory = Path.Combine(captures, name);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "frames.csv");
            File.WriteAllText(path, Frames);
            File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new { Scenario = notes, Mode = "Monitor" }));
            File.WriteAllText(Path.Combine(directory, "recorder.json"), JsonSerializer.Serialize(new { StartUtc = start }));
            return path;
        }

        void AssertDialog(string expected)
        {
            var dialog = (Microsoft.Win32.OpenFileDialog)typeof(MainWindow).GetMethod("CaptureDialog", Private)!.Invoke(window, null)!;
            if (!string.Equals(dialog.InitialDirectory, expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Capture picker opened in '{dialog.InitialDirectory}', expected '{expected}'.");
        }

        async Task ClickAsync(string name)
        {
            ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((bool)typeof(MainWindow).GetField("busy", Private)!.GetValue(window)!)
                await Task.Delay(10, timeout.Token);
            if (((TextBlock)window.FindName("StatusText")).Text.StartsWith("Action needed:", StringComparison.Ordinal))
                throw new Exception(((TextBlock)window.FindName("StatusText")).Text);
        }
    }

    private static T Property<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;

    private sealed class WindowScope(MainWindow window) : IDisposable
    {
        public MainWindow Window => window;
        public void Dispose() => window.Close();
    }
}
