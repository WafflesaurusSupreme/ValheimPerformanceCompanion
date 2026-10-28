using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Companion.Core;

namespace Companion.App;

public partial class App : Application
{
    private Mutex? guiInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--collector")
        { Shutdown(await PerfViewCollector.RunHelperAsync(e.Args[1])); return; }
        DispatcherUnhandledException += (_, args) => { AppLog.Write("Dispatcher exception", args.Exception); MessageBox.Show(args.Exception.Message + "\n\nDiagnostic saved in " + AppLog.DirectoryPath, "Performance Companion", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        bool rendering = e.Args.Length >= 3 && e.Args[0] == "--render";
        if (!rendering) AppLog.State("Starting desktop UI");
        if (!rendering)
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            guiInstance = new Mutex(true, "Local\\ValheimPerformanceCompanion." + user, out bool created);
            if (!created) { guiInstance.Dispose(); guiInstance = null; MessageBox.Show("Performance Companion is already running for this user.", "Performance Companion"); Shutdown(); return; }
        }
        var window = new MainWindow(rendering); MainWindow = window;
        if (!rendering) AppLog.State("Window constructed");
        if (e.Args.Length >= 3 && e.Args[0] == "--render")
        {
            try
            {
                await window.LoadCaptureAsync(e.Args[1]);
                window.Width = 1240; window.Height = 880;
                window.ShowInTaskbar = false; window.Left = -20000; window.Top = -20000; window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Show();
                if (e.Args.Length > 3 && e.Args[3] == "record") await window.PrepareRecordPreviewAsync();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.Measure(new Size(1240, 880)); window.Arrange(new Rect(0, 0, 1240, 880)); window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                int width = (int)Math.Ceiling(content.ActualWidth + 60), height = (int)Math.Ceiling(content.ActualHeight + 48);
                var drawing = new DrawingVisual();
                using (DrawingContext dc = drawing.RenderOpen())
                { dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height)); dc.DrawRectangle(new VisualBrush(content), null, new Rect(30, 24, content.ActualWidth, content.ActualHeight)); }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = new FileStream(e.Args[2], FileMode.CreateNew); encoder.Save(file);
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[2] + ".error.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        window.Show();
        AppLog.State("Window shown");
    }
    protected override void OnExit(ExitEventArgs e)
    { guiInstance?.ReleaseMutex(); guiInstance?.Dispose(); base.OnExit(e); }
}
