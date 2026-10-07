using Avalonia.Controls;
using Avalonia.Layout;
using Dubbing.App.Controls;

namespace Dubbing.App.Services;

public sealed partial class ProjectDialogs
{
    public async Task<(double Start, double End)?> EditVoiceReferenceAsync(IReadOnlyList<float> peaks, double windowStart, double windowEnd,
        double start, double end, Func<double, double, Task> preview, Action stop)
    {
        var dialog = new Window { Title = text["Adjust voice reference"], Width = 780, Height = 440, MinWidth = 620,
            Background = Avalonia.Media.Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var wave = new ReferenceWaveform { Height = 140, Peaks = peaks, WindowStart = windowStart, WindowEnd = windowEnd, Start = start, End = end };
        var from = new NumericUpDown { Minimum = (decimal)windowStart, Maximum = (decimal)windowEnd, Increment = .01m, FormatString = "F2", Value = (decimal)start, Width = 160 };
        var to = new NumericUpDown { Minimum = (decimal)windowStart, Maximum = (decimal)windowEnd, Increment = .01m, FormatString = "F2", Value = (decimal)end, Width = 160 };
        var status = new TextBlock();
        var apply = new Button { Content = text["Use reference"] };
        var play = new Button { Content = text["Play selection"] };
        var cancel = new Button { Content = text["Cancel"] };
        bool updating = false, closed = false;
        void Update()
        {
            if (updating) return;
            wave.Start = (double)(from.Value ?? 0); wave.End = (double)(to.Value ?? 0); wave.InvalidateVisual();
            double duration = wave.End-wave.Start;
            apply.IsEnabled = duration >= 1 && duration <= 10;
            play.IsEnabled = duration > 0;
            status.Text = $"{duration:F2} s · " + text["Reference must be between 1 and 10 seconds."];
        }
        from.ValueChanged += (_, _) => Update(); to.ValueChanged += (_, _) => Update();
        wave.RangeChanged += (a,b) => { updating = true; from.Value = (decimal)a; to.Value = (decimal)b; updating = false; Update(); };
        play.Click += async (_, _) =>
        {
            play.IsEnabled = false; apply.IsEnabled = false;
            try { await preview(wave.Start, wave.End); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { if (closed) stop(); else Update(); }
        };
        apply.Click += (_, _) => dialog.Close(((double, double)?)(wave.Start, wave.End));
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { closed = true; stop(); };
        dialog.Content = new StackPanel { Margin = new(20), Spacing = 14, Children = {
            new TextBlock { Text = text["Drag the white edges or edit the times. Listen before applying; avoid other voices."], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            wave, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = {
                new TextBlock { Text = text["Start (seconds)"] }, from, new TextBlock { Text = text["End (seconds)"] }, to } },
            status, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { play, apply, cancel } } } };
        Update();
        return await ShowDialogAsync<(double, double)?>(dialog);
    }
}
