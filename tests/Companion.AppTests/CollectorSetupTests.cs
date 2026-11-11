using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Companion.App;

internal static class CollectorSetupTests
{
    public static async Task RunAsync(string root)
    {
        var window = new MainWindow(true);
        int downloads = 0, detections = 0;
        Task Setup() { downloads++; return Task.CompletedTask; }
        await CollectorSetup.CheckAtStartupAsync(window.ShowCollectorSetupState, Setup, () => { detections++; return true; });
        if (downloads != 0 || detections != 1) throw new Exception("Installed collector startup triggered setup or failed to check service presence.");
        window.ShowCollectorSetupState(true);
        if (((FrameworkElement)window.FindName("CollectorSetupPanel")).Visibility != Visibility.Collapsed
            || !string.IsNullOrEmpty(((TextBlock)window.FindName("CollectorExplanation")).Text))
            throw new Exception("Installed collectors must not display the setup explainer.");
        await CollectorSetup.CheckAtStartupAsync(window.ShowCollectorSetupState, Setup, () => false);
        if (downloads != 1) throw new Exception("Missing collector startup did not trigger automatic setup.");
        string explanation = ((TextBlock)window.FindName("CollectorExplanation")).Text;
        if (((FrameworkElement)window.FindName("CollectorSetupPanel")).Visibility != Visibility.Visible
            || !new[] { "PerfView", "administrator", "other applications", "Monitor", "GitHub", "SHA-256" }.All(explanation.Contains))
            throw new Exception("Missing collector setup must explain permissions, privacy, download identity and the optional fallback.");
        window.ShowCollectorSetupState(true);
        if (((FrameworkElement)window.FindName("CollectorSetupPanel")).Visibility != Visibility.Collapsed)
            throw new Exception("Setup explanation remains visible after installation.");
        string version = typeof(CollectorSetup).Assembly.GetName().Version!.ToString(3);
        string name = $"ValheimPerformanceCompanion-{version}-win-x64.zip";
        string url = $"https://github.com/WafflesaurusSupreme/ValheimPerformanceCompanion/releases/download/v{version}-preview.1/{name}";
        string digest = "sha256:" + new string('a', 64);
        byte[] Metadata(string location, string hash) => JsonSerializer.SerializeToUtf8Bytes(new { assets = new[] { new { name, browser_download_url = location, digest = hash } } });
        var parsed = CollectorSetup.ParseRelease(Metadata(url, digest));
        if (parsed.Url != url || parsed.Hash != new string('a', 64)) throw new Exception("Valid release metadata rejected.");
        Reject(() => CollectorSetup.ParseRelease(Metadata("https://example.com/collector.zip", digest)));
        Reject(() => CollectorSetup.ParseRelease(Metadata(url, "sha256:bad")));
        Reject(() => CollectorSetup.ParseRelease(new byte[1024 * 1024 + 1]));
        Reject(() => CollectorSetup.ParseRelease(Encoding.UTF8.GetBytes("{\"assets\":[]}")));
        Archive(["ValheimPerformanceCompanion/Tools/PerfView.exe", "ValheimPerformanceCompanion/ValheimPerformanceCompanion.exe"], false);
        Archive(["ValheimPerformanceCompanion/../../escape.exe"], true);
        Archive(["ValheimPerformanceCompanion/file", "ValheimPerformanceCompanion/FILE"], true);
        Archive(["unexpected/file.exe"], true);
        Archive(["ValheimPerformanceCompanion/file:stream"], true);
        window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000;
        window.Show();
        foreach (bool present in new[] { false, true })
        {
            window.ShowCollectorSetupState(present); window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, present ? "collector-present.png" : "collector-missing.png")); png.Save(file);
        }
        window.Close();
        Console.WriteLine("PASS conditional collector explainer, official download identity, missing release, archive traversal/collision/ADS rejection.");

        void Archive(string[] names, bool reject)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                foreach (string entry in names) zip.CreateEntry(entry);
            bytes.Position = 0;
            using var read = new ZipArchive(bytes, ZipArchiveMode.Read);
            if (reject) Reject(() => CollectorSetup.ValidateArchive(read, Path.Combine(root, "installed")));
            else CollectorSetup.ValidateArchive(read, Path.Combine(root, "installed"));
        }
        static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Unsafe or incomplete setup input was accepted.");
        }
    }
}
