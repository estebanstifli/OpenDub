using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Dubbing.Core.Projects;
namespace Dubbing.App.Controls;

public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<DubSegment>?> SegmentsProperty = AvaloniaProperty.Register<TimelineControl, IReadOnlyList<DubSegment>?>(nameof(Segments));
    public static readonly StyledProperty<IReadOnlyList<float>?> PeaksProperty = AvaloniaProperty.Register<TimelineControl, IReadOnlyList<float>?>(nameof(Peaks));
    public static readonly StyledProperty<IReadOnlyList<float>?> BackgroundPeaksProperty = AvaloniaProperty.Register<TimelineControl, IReadOnlyList<float>?>(nameof(BackgroundPeaks));
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<TimelineControl, double>(nameof(Duration), 1);
    public static readonly StyledProperty<double> PlayheadProperty = AvaloniaProperty.Register<TimelineControl, double>(nameof(Playhead));
    public static readonly StyledProperty<string> SourceLabelProperty = AvaloniaProperty.Register<TimelineControl, string>(nameof(SourceLabel), "Original audio");
    public static readonly StyledProperty<string> BackgroundLabelProperty = AvaloniaProperty.Register<TimelineControl, string>(nameof(BackgroundLabel), "Background");
    public static readonly StyledProperty<string> SegmentLabelProperty = AvaloniaProperty.Register<TimelineControl, string>(nameof(SegmentLabel), "Dialogue");
    public IReadOnlyList<DubSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IReadOnlyList<float>? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public IReadOnlyList<float>? BackgroundPeaks { get => GetValue(BackgroundPeaksProperty); set => SetValue(BackgroundPeaksProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public string SourceLabel { get => GetValue(SourceLabelProperty); set => SetValue(SourceLabelProperty, value); }
    public string BackgroundLabel { get => GetValue(BackgroundLabelProperty); set => SetValue(BackgroundLabelProperty, value); }
    public string SegmentLabel { get => GetValue(SegmentLabelProperty); set => SetValue(SegmentLabelProperty, value); }
    public event Action<double>? SeekRequested;
    public event Action<string>? SegmentSelected;
    private bool dragging;
    public bool IsDragging => dragging;
    public static readonly StyledProperty<double> GutterProperty = AvaloniaProperty.Register<TimelineControl, double>(nameof(Gutter), 110);
    public double Gutter { get => GetValue(GutterProperty); set => SetValue(GutterProperty, value); }
    private double TrackWidth => Math.Max(1, Bounds.Width - Gutter - 8);
    static TimelineControl() => AffectsRender<TimelineControl>(SegmentsProperty, PeaksProperty, BackgroundPeaksProperty, DurationProperty, PlayheadProperty, SourceLabelProperty, BackgroundLabelProperty, SegmentLabelProperty);
    public TimelineControl() { ClipToBounds = true; Focusable = true; Cursor = new Cursor(StandardCursorType.Cross); }
    public double PositionToSeconds(double x) => Math.Clamp((x - Gutter) / TrackWidth, 0, 1) * Math.Max(0, Duration);
    private double X(double time) => Gutter + Math.Clamp(time / Math.Max(.001, Duration), 0, 1) * TrackWidth;
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush("#0A1220"), new Rect(Bounds.Size));
        void Label(string text, double x, double y, IBrush? color = null) => context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, color ?? Brush("#91A4BF")), new Point(x, y));
        var gridPen = new Pen(Brush("#243249"), 1);
        double ideal = Math.Max(1, Duration) * 90 / TrackWidth;
        double step = new[] { .1, .25, .5, 1d, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 }.FirstOrDefault(n => n >= ideal);
        if (step == 0) step = Math.Ceiling(ideal / 3600) * 3600;
        for (double t = 0; t <= Duration; t += step) { double x = X(t); context.DrawLine(gridPen, new(x, 27), new(x, 205)); Label(TimeSpan.FromSeconds(t).ToString(Duration >= 3600 ? "hh\\:mm\\:ss" : "mm\\:ss"), x + 4, 6); }
        void Wave(IReadOnlyList<float>? peaks, double y, string color)
        {
            context.FillRectangle(Brush("#101D2F"), new Rect(Gutter, y - 22, TrackWidth, 44), 4);
            context.DrawLine(new Pen(Brush("#273951")), new(Gutter, y), new(Gutter + TrackWidth, y));
            if (peaks is null || peaks.Count == 0) return;
            var pen = new Pen(Brush(color), Math.Max(1, TrackWidth / peaks.Count * .65));
            for (int i = 0; i < peaks.Count; i++) { double x = Gutter + i * TrackWidth / peaks.Count, h = Math.Clamp(peaks[i], 0, 1) * 20; context.DrawLine(pen, new(x, y - h), new(x, y + h)); }
        }
        Wave(Peaks, 60, "#4EBAC2"); Wave(BackgroundPeaks, 116, "#8B8BE8");
        if (Gutter > 5) using (context.PushClip(new Rect(0, 0, Gutter - 5, Bounds.Height))) { Label(SourceLabel, 10, 52); Label(BackgroundLabel, 10, 108); Label(SegmentLabel, 10, 170); }
        foreach (var segment in Segments ?? [])
        {
            var rect = new Rect(X(segment.Start.TotalSeconds), 155, Math.Max(2, X(segment.End.TotalSeconds) - X(segment.Start.TotalSeconds)), 40);
            context.DrawRectangle(Brush(segment.ExcludedFromDubbing ? "#283142" : "#153C61"), new Pen(Brush(segment.ExcludedFromDubbing ? "#465269" : "#398DF5")), rect, 4, 4);
            if (rect.Width > 22) using (context.PushClip(rect.Deflate(4))) Label(string.IsNullOrWhiteSpace(segment.Translation.Text) ? segment.Source.Text : segment.Translation.Text, rect.X + 7, 167, Brush("#DBEAFF"));
        }
        double head = X(Playhead); context.DrawLine(new Pen(Brushes.White, 2), new(head, 24), new(head, 205)); context.FillRectangle(Brushes.White, new Rect(head - 5, 22, 10, 8), 2);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); dragging = true; e.Pointer.Capture(this); var point = e.GetPosition(this);
        if (point.Y >= 155) { var time = PositionToSeconds(point.X); var selected = Segments?.FirstOrDefault(s => s.Start.TotalSeconds <= time && s.End.TotalSeconds >= time); if (selected is not null) SegmentSelected?.Invoke(selected.Id); }
        SeekRequested?.Invoke(PositionToSeconds(point.X));
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (dragging) SeekRequested?.Invoke(PositionToSeconds(e.GetPosition(this).X)); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { dragging = false; e.Pointer.Capture(null); base.OnPointerReleased(e); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.Key is Key.Left or Key.Right) { SeekRequested?.Invoke(Math.Clamp(Playhead + (e.Key == Key.Left ? -1 : 1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : .1), 0, Duration)); e.Handled = true; } base.OnKeyDown(e); }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
