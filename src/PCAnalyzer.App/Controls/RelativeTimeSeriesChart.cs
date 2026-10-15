using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.Controls;

/// <summary>Plots PresentMon's original relative clock without mapping it to an invented UTC session.</summary>
public sealed class RelativeTimeSeriesChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(RelativeSeries),
        typeof(RelativeTimeSeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, _) => ((RelativeTimeSeriesChart)owner).Reset()));
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(16, 24, 33));
    private static readonly Brush Labels = new SolidColorBrush(Color.FromRgb(147, 168, 188));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(100, 214, 201));
    private static readonly Pen Grid = new(new SolidColorBrush(Color.FromRgb(48, 65, 81)), 1);
    private RelativePoint[] _raw = [];
    private StreamGeometry? _geometry;
    private Size _cachedSize;
    private Rect _plot;
    private double _min, _max, _start, _end;
    private RelativePoint? _hover;
    private RelativePoint? _singlePoint;

    public RelativeSeries? Series { get => (RelativeSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public RelativeTimeSeriesChart()
    {
        MouseMove += Pointer;
        MouseLeave += (_, _) => { _hover = null; ToolTip = null; InvalidateVisual(); };
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, 60000);
    }
    private void Reset()
    {
        _raw = (Series?.Points ?? []).Where(p => double.IsFinite(p.TimeMilliseconds)).TakeLast(10_000).OrderBy(p => p.TimeMilliseconds).ToArray();
        _geometry = null; _hover = null;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (ActualWidth < 100 || ActualHeight < 70) return;
        if (_geometry is null || _cachedSize != RenderSize) PrepareGeometry();
        if (_geometry is null) { Label(drawing, "Sem curva relativa válida nesta operação", new(12, ActualHeight / 2 - 8)); return; }
        for (var tick = 0; tick <= 4; tick++)
        {
            var y = _plot.Bottom - _plot.Height * tick / 4d;
            drawing.DrawLine(Grid, new(_plot.Left, y), new(_plot.Right, y));
            Label(drawing, (_min + (_max - _min) * tick / 4).ToString("0.##"), new(2, y - 6));
        }
        Label(drawing, Series?.Unit ?? "", new(3, 0));
        Label(drawing, $"{_start:0.##} ms", new(_plot.Left, _plot.Bottom + 7));
        Label(drawing, $"{_end:0.##} ms", new(Math.Max(_plot.Left, _plot.Right - 77), _plot.Bottom + 7));
        drawing.DrawGeometry(null, new Pen(Accent, 1.4), _geometry);
        if (_singlePoint is { } single) drawing.DrawEllipse(Accent, null, Coordinate(single), 3, 3);
        if (_hover is { Value: { } value } point && double.IsFinite(value))
        {
            var location = Coordinate(point);
            drawing.DrawLine(new Pen(Labels, 1) { DashStyle = DashStyles.Dot }, new(location.X, _plot.Top), new(location.X, _plot.Bottom));
            drawing.DrawEllipse(Accent, new Pen(BackgroundBrush, 1), location, 3, 3);
        }
    }
    private void PrepareGeometry()
    {
        _cachedSize = RenderSize;
        _plot = new(58, 15, ActualWidth - 73, ActualHeight - 48);
        var finite = _raw.Where(Valid).ToArray();
        if (finite.Length == 0) { _geometry = null; return; }
        _singlePoint = finite.Length == 1 ? finite[0] : null;
        _min = finite.Min(p => p.Value!.Value); _max = finite.Max(p => p.Value!.Value);
        if (_max - _min < .00000001) { var pad = Math.Max(Math.Abs(_max) * .05, .5); _min -= pad; _max += pad; }
        _start = _raw[0].TimeMilliseconds; _end = _raw[^1].TimeMilliseconds;
        if (_end <= _start) _end = _start + 1;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var penStarted = false;
            // Retain minimum and maximum in every bucket, so a short stutter is not averaged away.
            var bucketSize = Math.Max(1, (int)Math.Ceiling(_raw.Length / 800d));
            for (var offset = 0; offset < _raw.Length; offset += bucketSize)
            {
                var indices = new SortedSet<int> { offset, Math.Min(offset + bucketSize - 1, _raw.Length - 1) };
                int? smallest = null, largest = null;
                for (var index = offset; index < Math.Min(offset + bucketSize, _raw.Length); index++)
                {
                    if (!Valid(_raw[index])) { indices.Add(index); continue; }
                    if (smallest is null || _raw[index].Value < _raw[smallest.Value].Value) smallest = index;
                    if (largest is null || _raw[index].Value > _raw[largest.Value].Value) largest = index;
                }
                if (smallest.HasValue) indices.Add(smallest.Value);
                if (largest.HasValue) indices.Add(largest.Value);
                foreach (var index in indices)
                {
                    var point = _raw[index];
                    if (!Valid(point)) { penStarted = false; continue; }
                    var location = Coordinate(point);
                    if (!penStarted) { context.BeginFigure(location, false, false); penStarted = true; }
                    else context.LineTo(location, true, false);
                }
            }
        }
        geometry.Freeze(); _geometry = geometry;
    }
    private Point Coordinate(RelativePoint point) => new(_plot.Left + _plot.Width * (point.TimeMilliseconds - _start) / (_end - _start),
        _plot.Bottom - _plot.Height * (point.Value!.Value - _min) / (_max - _min));
    private static bool Valid(RelativePoint point) => point.Value is { } value && double.IsFinite(value);
    private void Label(DrawingContext drawing, string value, Point position) => drawing.DrawText(
        new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Labels,
            VisualTreeHelper.GetDpi(this).PixelsPerDip), position);
    private void Pointer(object sender, MouseEventArgs args)
    {
        var position = args.GetPosition(this);
        if (_geometry is null || !_plot.Contains(position) || _raw.Length == 0) { _hover = null; ToolTip = null; InvalidateVisual(); return; }
        var relative = _start + (position.X - _plot.Left) / _plot.Width * (_end - _start);
        var lo = 0; var hi = _raw.Length - 1;
        while (lo < hi) { var mid = (lo + hi) / 2; if (_raw[mid].TimeMilliseconds < relative) lo = mid + 1; else hi = mid; }
        if (lo > 0 && Math.Abs(_raw[lo - 1].TimeMilliseconds - relative) < Math.Abs(_raw[lo].TimeMilliseconds - relative)) lo--;
        _hover = _raw[lo];
        ToolTip = $"{Series?.Name}\nt={_hover.TimeMilliseconds:0.###} ms · {(_hover.Value.HasValue ? _hover.Value.Value.ToString("0.###") : "indisponível")} {Series?.Unit}\n{Series?.TimeBasis}\n{Series?.Source}";
        InvalidateVisual();
    }
}
