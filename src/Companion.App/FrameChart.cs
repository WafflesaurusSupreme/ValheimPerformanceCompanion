using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Companion.Core;

namespace Companion.App;

public sealed class FrameChart : FrameworkElement
{
    private IReadOnlyList<Frame> frames = [];
    private double start, end = 1;
    private Frame? selected;
    public event Action<Frame>? FrameSelected;
    public double BudgetMs { get; set; } = 16.67;
    public IReadOnlyList<LogEvent> Markers { get; set; } = [];
    public void SetFrames(IReadOnlyList<Frame> value)
    { frames = value; start = frames.FirstOrDefault()?.Seconds ?? 0; end = Math.Max(start + .01, frames.LastOrDefault()?.Seconds ?? 1); selected = null; InvalidateVisual(); }
    public void SelectFrame(Frame frame) { selected = frame; FrameSelected?.Invoke(frame); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        if (width < 80 || height < 40) return;
        const double left = 45, bottom = 24; double plotWidth = width - left - 8, plotHeight = height - bottom - 10;
        var muted = new SolidColorBrush(Color.FromRgb(142, 158, 180));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(43, 56, 75)), 1);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(17, 26, 38)), null, new Rect(0, 0, width, height), 8, 8);
        Frame[] visible = frames.Where(f => f.Seconds >= start && f.Seconds <= end).ToArray();
        double maximum = Math.Max(BudgetMs * 2, visible.Length > 0 ? Analyzer.Percentile(visible.Select(f => f.Milliseconds).Order().ToArray(), .99) * 1.35 : 33.3);
        maximum = Math.Min(500, Math.Max(20, maximum));
        for (int i = 0; i < 4; i++)
        {
            double y = 8 + plotHeight * i / 3;
            dc.DrawLine(grid, new Point(left, y), new Point(width - 8, y));
            Text(dc, (maximum * (1 - i / 3.0)).ToString("F0", CultureInfo.CurrentCulture), new Point(5, y - 7), muted);
        }
        Text(dc, "ms", new Point(5, height - 20), muted);
        Text(dc, start.ToString("F1") + " s", new Point(left, height - 20), muted);
        Text(dc, end.ToString("F1") + " s", new Point(width - 60, height - 20), muted);
        double budgetY = 8 + plotHeight * (1 - Math.Min(1, BudgetMs / maximum));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(184, 147, 84)), 1) { DashStyle = DashStyles.Dash }, new Point(left, budgetY), new Point(width - 8, budgetY));
        if (visible.Length == 0) { Text(dc, "No frame data", new Point(left + 16, height / 2), muted); return; }
        int buckets = Math.Max(2, (int)plotWidth);
        var values = new double[buckets];
        foreach (Frame frame in visible)
        { int i = Math.Clamp((int)((frame.Seconds - start) / (end - start) * (buckets - 1)), 0, buckets - 1); values[i] = Math.Max(values[i], frame.Milliseconds); }
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(left, 8 + plotHeight), false, false);
            for (int i = 0; i < values.Length; i++) context.LineTo(new Point(left + i * plotWidth / (buckets - 1), 8 + plotHeight * (1 - Math.Min(maximum, values[i]) / maximum)), true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(110, 231, 199)), 1.5), geometry);
        void Overlay(Func<Frame, double?> selector, Color color)
        {
            var counters = new double?[buckets];
            foreach (Frame frame in visible)
            {
                double? value = selector(frame); if (!value.HasValue) continue;
                int i = Math.Clamp((int)((frame.Seconds - start) / (end - start) * (buckets - 1)), 0, buckets - 1);
                counters[i] = Math.Max(counters[i] ?? 0, value.Value);
            }
            var line = new StreamGeometry();
            using (StreamGeometryContext context = line.Open())
            {
                bool begun = false;
                for (int i = 0; i < counters.Length; i++)
                {
                    if (counters[i] is not double value) continue;
                    var point = new Point(left + i * plotWidth / (buckets - 1), 8 + plotHeight * (1 - Math.Min(maximum, value) / maximum));
                    if (!begun) { context.BeginFigure(point, false, false); begun = true; } else context.LineTo(point, true, false);
                }
            }
            line.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1), line);
        }
        Overlay(f => f.CpuMs, Color.FromArgb(150, 118, 169, 255));
        Overlay(f => f.GpuMs, Color.FromArgb(190, 247, 199, 125));
        int? previousGc = null;
        foreach (Frame frame in visible)
        {
            if (frame.Gc0.HasValue && previousGc.HasValue && frame.Gc0 > previousGc)
            { double x = left + (frame.Seconds - start) / (end - start) * plotWidth; dc.DrawLine(new Pen(Brushes.IndianRed, 2), new Point(x, plotHeight + 3), new Point(x, plotHeight + 8)); }
            previousGc = frame.Gc0;
        }
        foreach (LogEvent marker in Markers.Where(m => m.Category == "Marker" && m.Seconds >= start && m.Seconds <= end))
        { double x = left + (marker.Seconds!.Value - start) / (end - start) * plotWidth; dc.DrawLine(new Pen(Brushes.Goldenrod, 1), new Point(x, 8), new Point(x, plotHeight + 8)); }
        if (selected is not null)
        {
            double x = left + (selected.Seconds - start) / (end - start) * plotWidth;
            dc.DrawLine(new Pen(Brushes.White, 1), new Point(x, 8), new Point(x, plotHeight + 8));
        }
    }
    private void Text(DrawingContext dc, string text, Point position, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), position);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (frames.Count == 0) return;
        if (e.ClickCount == 2) { start = frames[0].Seconds; end = Math.Max(start + .01, frames[^1].Seconds); InvalidateVisual(); return; }
        double at = start + Math.Clamp((e.GetPosition(this).X - 45) / Math.Max(1, ActualWidth - 53), 0, 1) * (end - start);
        double bucket = (end - start) / Math.Max(1, ActualWidth - 53);
        selected = frames.Where(f => Math.Abs(f.Seconds - at) < bucket * 2).OrderByDescending(f => f.Milliseconds).FirstOrDefault()
            ?? frames.MinBy(f => Math.Abs(f.Seconds - at));
        if (selected is not null) FrameSelected?.Invoke(selected);
        InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (frames.Count == 0) return;
        double anchor = start + Math.Clamp((e.GetPosition(this).X - 45) / Math.Max(1, ActualWidth - 53), 0, 1) * (end - start);
        double span = Math.Clamp((end - start) * (e.Delta > 0 ? .65 : 1.55), .2, Math.Max(.2, frames[^1].Seconds - frames[0].Seconds));
        start = Math.Clamp(anchor - span / 2, frames[0].Seconds, Math.Max(frames[0].Seconds, frames[^1].Seconds - span));
        end = start + span; e.Handled = true; InvalidateVisual();
    }
}
