using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Dubbing.App.Controls;

public sealed record WaveformRegion(double Start, double End, string Label);

public sealed class ReferenceWaveform : Control
{
    public IReadOnlyList<float> Peaks { get; set; } = [];
    public double WindowStart { get; set; }
    public double WindowEnd { get; set; } = 1;
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public IReadOnlyList<WaveformRegion> ExistingRegions { get; set; } = [];
    public bool HasConflict { get; set; }
    public bool AllowMove { get; set; }
    public double? SplitPoint { get; set; }
    public event Action<double>? SplitChanged;
    public event Action<double, double>? RangeChanged;
    private bool dragging, left, moving;
    private double anchor, initialStart, initialEnd;
    private double X(double time) => (Math.Clamp(time, WindowStart, WindowEnd) - WindowStart) / Math.Max(.001, WindowEnd - WindowStart) * Bounds.Width;
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush.Parse("#09111E"), new Rect(Bounds.Size));
        var visibleRegions = ExistingRegions.Where(r => r.End > WindowStart && r.Start < WindowEnd).ToArray();
        foreach (var region in visibleRegions)
            context.FillRectangle(Brush.Parse("#354255"), new Rect(X(region.Start), 4, Math.Max(0, X(region.End)-X(region.Start)), Bounds.Height-8));
        context.FillRectangle(Brush.Parse(HasConflict ? "#803D46" : "#244F76"), new Rect(X(Start), 0, Math.Max(0, X(End)-X(Start)), Bounds.Height));
        if (SplitPoint is { } split)
            context.FillRectangle(Brush.Parse("#654B25"), new Rect(X(split), 0, Math.Max(0, X(End)-X(split)), Bounds.Height));
        var pen = new Pen(Brush.Parse("#64D8CB"), 1);
        for (int i = 0; i < Peaks.Count; i++)
        {
            double x = (double)i / Math.Max(1, Peaks.Count - 1) * Bounds.Width, h = Math.Clamp(Math.Abs(Peaks[i]), 0, 1) * Bounds.Height * .44;
            context.DrawLine(pen, new(x, Bounds.Height/2-h), new(x, Bounds.Height/2+h));
        }
        foreach (var region in visibleRegions)
        {
            var rect = new Rect(X(region.Start), 4, Math.Max(0, X(region.End)-X(region.Start)), Bounds.Height-8);
            context.DrawRectangle(null, new Pen(Brush.Parse("#9BAAC1"), 1.5), rect);
            if (rect.Width > 20)
            {
                using var clip = context.PushClip(rect);
                context.DrawText(new FormattedText(region.Label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, Brushes.LightSteelBlue), new Point(rect.X + 5, rect.Y + 3));
            }
        }
        if (SplitPoint is { } marker)
            context.DrawLine(new Pen(Brushes.Gold, 3), new(X(marker), 0), new(X(marker), Bounds.Height));
        foreach (double time in new[] { Start, End })
            context.DrawLine(new Pen(HasConflict ? Brushes.Salmon : Brushes.White, 4), new(X(time), 0), new(X(time), Bounds.Height));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (End-Start < .01 || WindowEnd <= WindowStart) return;
        double x = e.GetPosition(this).X;
        anchor = x; initialStart = Start; initialEnd = End;
        moving = AllowMove && SplitPoint is null && x > X(Start)+8 && x < X(End)-8;
        left = Math.Abs(x-X(Start)) <= Math.Abs(x-X(End)); dragging = true;
        e.Pointer.Capture(this); Move(x); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); double x = e.GetPosition(this).X;
        Cursor = new Cursor(SplitPoint is not null || Math.Abs(x-X(Start)) < 9 || Math.Abs(x-X(End)) < 9 ? StandardCursorType.SizeWestEast
            : AllowMove && x > X(Start) && x < X(End) ? StandardCursorType.SizeAll : StandardCursorType.Arrow);
        if (dragging) Move(x);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); dragging = false; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }
    private void Move(double x)
    {
        double t = WindowStart + Math.Clamp(x/Math.Max(1, Bounds.Width), 0, 1) * (WindowEnd-WindowStart);
        if (SplitPoint is not null)
        {
            SplitPoint = Math.Clamp(t, Start + .1, End - .1); SplitChanged?.Invoke(SplitPoint.Value); InvalidateVisual(); return;
        }
        if (moving)
        {
            double delta = Math.Clamp((x-anchor)/Math.Max(1, Bounds.Width)*(WindowEnd-WindowStart), WindowStart-initialStart, WindowEnd-initialEnd);
            Start = initialStart+delta; End = initialEnd+delta;
        }
        else if (left) Start = Math.Min(t, End-.01); else End = Math.Max(t, Start+.01);
        RangeChanged?.Invoke(Start, End); InvalidateVisual();
    }
}
