using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCAnalyzer.Core;
using PCAnalyzer.App.ViewModels;

namespace PCAnalyzer.App.Controls;

/// <summary>Bounded, timestamp-based chart; gaps are preserved instead of rendered as zero.</summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<MetricSample>), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (_, _) => { }));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(42, 56, 80));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(162, 176, 199));
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(120, 168, 255));
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(21, 30, 45));
    private readonly List<(MetricSample Sample, Point Point)> _points = [];
    private Rect _plot;
    private Point? _hover;
    private string? _axisKey;
    private double _axisMinimum = double.NaN, _axisMaximum = double.NaN;
    public IReadOnlyList<MetricSample>? Samples { get => (IReadOnlyList<MetricSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    public TimeSeriesChart()
    {
        SnapsToDevicePixels = true;
        MouseMove += OnPointer;
        MouseLeave += (_, _) => { _hover = null; ToolTip = null; InvalidateVisual(); };
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, 60000);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        _points.Clear();
        if (ActualWidth < 100 || ActualHeight < 70) return;
        _plot = new(68, 20, ActualWidth - 86, ActualHeight - 55);
        var samples = (Samples ?? []).TakeLast(600).OrderBy(s => s.Timestamp).ToArray();
        var available = samples.Where(Valid).ToArray();
        if (available.Length == 0)
        {
            Text(drawing, "Nenhuma leitura disponível para este gráfico", new Point(20, ActualHeight / 2 - 10), 12);
            return;
        }
        var minimum = available.Min(s => s.Value!.Value);
        var maximum = available.Max(s => s.Value!.Value);
        if (available[0].Unit == "%" && minimum >= 0 && maximum <= 100) { minimum = 0; maximum = 100; }
        else
        {
            minimum = minimum >= 0 ? 0 : -NiceCeiling(Math.Abs(minimum));
            maximum = maximum <= 0 ? 1 : NiceCeiling(maximum);
            var axisKey = available[0].Key + "|" + available[0].Unit;
            if (_axisKey != axisKey) { _axisKey = axisKey; _axisMinimum = minimum; _axisMaximum = maximum; }
            else { _axisMinimum = Math.Min(_axisMinimum, minimum); _axisMaximum = Math.Max(_axisMaximum, maximum); }
            minimum = _axisMinimum; maximum = _axisMaximum;
        }
        var scale = MetricPresentation.Scale(available[0].Unit, Math.Max(Math.Abs(minimum), Math.Abs(maximum)));
        var first = samples[0].Timestamp;
        var last = samples[^1].Timestamp;
        var span = Math.Max((last - first).TotalMilliseconds, 1);
        for (var tick = 0; tick <= 4; tick++)
        {
            var y = _plot.Bottom - _plot.Height * tick / 4;
            drawing.DrawLine(new Pen(GridBrush, 1), new(_plot.Left, y), new(_plot.Right, y));
            var label = MetricPresentation.Number((minimum + (maximum - minimum) * tick / 4) / scale.Factor);
            Text(drawing, label, new(3, y - 7), 11);
        }
        Text(drawing, scale.Unit, new(4, 0), 11);
        Text(drawing, first.LocalDateTime.ToString("HH:mm:ss"), new(_plot.Left, _plot.Bottom + 8), 10);
        Text(drawing, last.LocalDateTime.ToString("HH:mm:ss"), new(Math.Max(_plot.Left, _plot.Right - 57), _plot.Bottom + 8), 10);
        Point? previous = null;
        DateTimeOffset? previousTime = null;
        var linePen = new Pen(LineBrush, 1.7);
        foreach (var sample in samples)
        {
            if (!Valid(sample)) { previous = null; previousTime = null; continue; }
            var point = new Point(_plot.Left + _plot.Width * (sample.Timestamp - first).TotalMilliseconds / span,
                _plot.Bottom - _plot.Height * (sample.Value!.Value - minimum) / (maximum - minimum));
            // A collector gap is not evidence of a continuous reading.
            if (previousTime.HasValue && sample.SamplingInterval > TimeSpan.Zero
                && sample.Timestamp - previousTime.Value > sample.SamplingInterval * 3) previous = null;
            if (previous is { } before) drawing.DrawLine(linePen, before, point);
            else drawing.DrawEllipse(LineBrush, null, point, 2.5, 2.5);
            _points.Add((sample, point)); previous = point; previousTime = sample.Timestamp;
        }
        if (_hover is { } hover && _plot.Contains(hover) && _points.Count > 0)
        {
            var nearest = _points.MinBy(p => Math.Abs(p.Point.X - hover.X));
            drawing.DrawLine(new Pen(LabelBrush, 1) { DashStyle = DashStyles.Dot }, new(nearest.Point.X, _plot.Top), new(nearest.Point.X, _plot.Bottom));
            drawing.DrawEllipse(LineBrush, new Pen(BackgroundBrush, 1.5), nearest.Point, 4, 4);
        }
    }
    private static bool Valid(MetricSample sample) => sample.Status == Availability.Available && sample.Value is { } value && double.IsFinite(value);
    private static double NiceCeiling(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(value, .001))));
        var scaled = value / magnitude;
        return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * magnitude;
    }
    private void Text(DrawingContext drawing, string value, Point point, double size)
        => drawing.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    private void OnPointer(object sender, MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        if (_plot.Contains(_hover.Value) && _points.Count > 0)
        {
            var nearest = _points.MinBy(p => Math.Abs(p.Point.X - _hover.Value.X)).Sample;
            var display = MetricPresentation.Format(nearest);
            ToolTip = $"{MetricPresentation.Name(nearest)}\n{display.Value} {display.Unit} · {nearest.Timestamp.LocalDateTime:HH:mm:ss.fff}\n{nearest.Source} · intervalo {nearest.SamplingInterval.TotalSeconds:0.###} s\n{nearest.Method}\n{nearest.Key}\n{nearest.Limitation}";
        }
        else ToolTip = null;
        InvalidateVisual();
    }
}
