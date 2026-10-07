using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Dubbing.App.ViewModels;
using System.Globalization;

namespace Dubbing.App.Controls;

public sealed record OverlapRegion(string Id, int Number, double Start, double End, double AudioEnd);
public sealed class OverlapWaveform : Control
{
    public IReadOnlyList<OverlapWaveSlice> Waves { get; set; } = [];
    public IReadOnlyList<OverlapRegion> Regions { get; set; } = [];
    public string SelectedId { get; set; } = "";
    public double WindowStart { get; set; }
    public double WindowEnd { get; set; } = 1;
    public double Playhead { get; set; }
    public bool IsDub { get; set; }
    public event Action<double, double>? RangeChanged;
    public event Action? RangeCompleted;
    public event Action<string>? SegmentSelected;
    public event Action<double>? SeekRequested;
    private bool dragging, left;
    private double start, end;
    private double X(double time) => (time - WindowStart) / Math.Max(.001, WindowEnd - WindowStart) * Bounds.Width;
    private OverlapRegion? Selected => Regions.FirstOrDefault(r => r.Id == SelectedId);
    private bool Handle(double x) => Selected is { } selected && (Math.Abs(x - X(selected.Start)) < 9 || Math.Abs(x - X(selected.End)) < 9);
    public override void Render(DrawingContext context)
    {
        using var clip = context.PushClip(new Rect(Bounds.Size));
        context.FillRectangle(Brush.Parse("#09111E"), new Rect(Bounds.Size));
        foreach (var r in Regions.OrderBy(r => r.Id == SelectedId))
        {
            bool selected = r.Id == SelectedId;
            var rect = new Rect(X(r.Start), 22, Math.Max(1, X(r.End) - X(r.Start)), Bounds.Height - 24);
            context.DrawRectangle(Brush.Parse(selected ? "#554B381B" : "#44366591"), new Pen(Brush.Parse(selected ? "#FFCC66" : "#5891D6"), selected ? 2 : 1), rect);
            if (IsDub && r.AudioEnd > r.End + .002)
                context.FillRectangle(Brush.Parse("#55F06060"), new Rect(X(r.End), 25, Math.Max(1, X(r.AudioEnd) - X(r.End)), Bounds.Height - 28));
            context.DrawText(new FormattedText($"#{r.Number}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
                selected ? Brush.Parse("#FFCC66") : Brushes.LightSteelBlue), new(X(r.Start) + 6, 27));
        }
        var pen = new Pen(Brush.Parse(IsDub ? "#ACA6FF" : "#64D8CB"), 1);
        foreach (var wave in Waves)
            for (int i = 0; i < wave.Peaks.Count; i++)
            {
                double x = X(wave.Start + (wave.End - wave.Start) * i / Math.Max(1, wave.Peaks.Count - 1));
                double h = Math.Clamp(Math.Abs(wave.Peaks[i]), 0, 1) * (Bounds.Height - 46) * .46, mid = (Bounds.Height + 32) / 2;
                context.DrawLine(pen, new(x, mid - h), new(x, mid + h));
            }
        if (Selected is { } active)
            foreach (double time in new[] { active.Start, active.End })
                context.DrawLine(new Pen(Brush.Parse("#FFCC66"), 5), new(X(time), 23), new(X(time), Bounds.Height - 2));
        for (int i = 0; i <= 6; i++)
        {
            double time = WindowStart + (WindowEnd - WindowStart) * i / 6;
            string label = TimeSpan.FromSeconds(time).ToString(@"mm\:ss\.fff");
            context.DrawText(new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.LightSteelBlue),
                new(Math.Clamp(X(time), 0, Math.Max(0, Bounds.Width - 75)), 2));
        }
        if (Playhead >= WindowStart && Playhead <= WindowEnd)
            context.DrawLine(new Pen(Brushes.White, 2), new(X(Playhead), 0), new(X(Playhead), Bounds.Height));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        double x = e.GetPosition(this).X;
        if (Handle(x) && Selected is { } selected)
        {
            start = selected.Start; end = selected.End; left = Math.Abs(x - X(start)) <= Math.Abs(x - X(end));
            dragging = true; e.Pointer.Capture(this);
        }
        else
        {
            double time = WindowStart + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * (WindowEnd - WindowStart);
            var selectedRegion = Regions.OrderByDescending(r => r.Id == SelectedId).FirstOrDefault(r => time >= r.Start && time <= r.End);
            if (selectedRegion is not null) SegmentSelected?.Invoke(selectedRegion.Id);
            SeekRequested?.Invoke(time);
        }
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); double x = e.GetPosition(this).X;
        Cursor = new Cursor(dragging || Handle(x) ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
        if (!dragging) return;
        double time = WindowStart + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * (WindowEnd - WindowStart);
        if (left) start = Math.Min(time, end - .01); else end = Math.Max(time, start + .01);
        RangeChanged?.Invoke(start, end);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); bool changed = dragging; dragging = false; e.Pointer.Capture(null);
        if (changed) RangeCompleted?.Invoke();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { dragging = false; base.OnPointerCaptureLost(e); }
}
