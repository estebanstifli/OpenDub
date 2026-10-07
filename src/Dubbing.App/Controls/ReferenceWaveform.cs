using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Dubbing.App.Controls;

public sealed class ReferenceWaveform : Control
{
    public IReadOnlyList<float> Peaks { get; set; } = [];
    public double WindowStart { get; set; }
    public double WindowEnd { get; set; } = 1;
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public event Action<double, double>? RangeChanged;
    private bool dragging, left;
    private double X(double time) => (Math.Clamp(time, WindowStart, WindowEnd) - WindowStart) / Math.Max(.001, WindowEnd - WindowStart) * Bounds.Width;
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush.Parse("#09111E"), new Rect(Bounds.Size));
        context.FillRectangle(Brush.Parse("#244F76"), new Rect(X(Start), 0, Math.Max(0, X(End)-X(Start)), Bounds.Height));
        var pen = new Pen(Brush.Parse("#64D8CB"), 1);
        for (int i = 0; i < Peaks.Count; i++)
        {
            double x = (double)i / Math.Max(1, Peaks.Count - 1) * Bounds.Width, h = Math.Clamp(Math.Abs(Peaks[i]), 0, 1) * Bounds.Height * .44;
            context.DrawLine(pen, new(x, Bounds.Height/2-h), new(x, Bounds.Height/2+h));
        }
        foreach (double time in new[] { Start, End })
            context.DrawLine(new Pen(Brushes.White, 4), new(X(time), 0), new(X(time), Bounds.Height));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        double x = e.GetPosition(this).X;
        left = Math.Abs(x-X(Start)) <= Math.Abs(x-X(End)); dragging = true;
        e.Pointer.Capture(this); Move(x); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (dragging) Move(e.GetPosition(this).X); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); dragging = false; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }
    private void Move(double x)
    {
        double t = WindowStart + Math.Clamp(x/Math.Max(1, Bounds.Width), 0, 1) * (WindowEnd-WindowStart);
        if (left) Start = Math.Min(t, End-.01); else End = Math.Max(t, Start+.01);
        RangeChanged?.Invoke(Start, End); InvalidateVisual();
    }
}
