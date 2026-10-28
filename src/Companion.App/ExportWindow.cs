using System.Windows;
using System.Windows.Controls;
using Companion.Core;

namespace Companion.App;

public sealed class ExportWindow : Window
{
    private readonly CheckBox raw = new() { Content = "Include unredacted raw logs, recorder files and ETW traces", Margin = new Thickness(0, 18, 0, 8) };
    public bool IncludeRaw => raw.IsChecked == true;
    public ExportWindow(Analysis analysis)
    {
        Title = "Review before sharing"; Width = 720; Height = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush"); Foreground = System.Windows.Media.Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(24) }; Content = layout;
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        footer.Children.Add(raw);
        footer.Children.Add(new TextBlock { Text = "Reports include code investigation notes, managed IL and automatically redacted diagnostic measurements. Review the preview before sharing. Optional raw files remain unredacted and can contain system-wide process names, paths and identifiers.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var save = new Button { Content = "Export ZIP", HorizontalAlignment = HorizontalAlignment.Right }; save.Click += (_, _) => DialogResult = true; footer.Children.Add(save);
        var title = new TextBlock { Text = "Sharing preview", FontSize = 24, Margin = new Thickness(0, 0, 0, 14) }; DockPanel.SetDock(title, Dock.Top); layout.Children.Add(title);
        layout.Children.Add(new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = ReportExporter.Redact(string.Join("\n\n", analysis.Findings.Select(f => f.Title + "\n" + f.Evidence)) + "\n\nLOG PREVIEW\n" + string.Join("\n", analysis.Capture.Events.Take(40).Select(e => e.Message))) });
    }
}
