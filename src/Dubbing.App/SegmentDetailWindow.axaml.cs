using Avalonia.Controls;
using Avalonia.Interactivity;
using Dubbing.App.ViewModels;
using Dubbing.App.Localization;

namespace Dubbing.App;

public partial class SegmentDetailWindow : Window
{
    private readonly TextCatalog text = new();
    private readonly SegmentDetailViewModel model = null!;
    public SegmentDetailWindow() { InitializeComponent(); }
    private bool closed, previewing;
    public SegmentDetailWindow(SegmentDetailViewModel model, TextCatalog text)
    {
        this.model = model; this.text = text;
        InitializeComponent(); DataContext = model;
        SegmentWave.Peaks = model.Peaks; SegmentWave.WindowStart = model.WindowStart; SegmentWave.WindowEnd = model.WindowEnd;
        void RefreshWave() { SegmentWave.Start = model.Editor.StartSeconds; SegmentWave.End = model.Editor.EndSeconds; SegmentWave.InvalidateVisual(); }
        RefreshWave();
        model.Editor.PropertyChanged += (_, _) => RefreshWave();
        SegmentWave.RangeChanged += (a,b) => { model.Editor.StartSeconds = a; model.Editor.EndSeconds = b; };
        Closed += (_, _) => { closed = true; model.Stop(); };
    }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void ApplyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (previewing) return;
            if (!double.IsFinite(model.Editor.StartSeconds) || !double.IsFinite(model.Editor.EndSeconds) ||
                model.Editor.StartSeconds < 0 || model.Editor.EndSeconds <= model.Editor.StartSeconds || model.Editor.EndSeconds > model.VideoDuration)
                throw new InvalidOperationException(text["Segment range must stay inside the video."]);
            using var json = System.Text.Json.JsonDocument.Parse(model.Editor.AdvancedOptions);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidOperationException(text["Advanced options must be a JSON object."]);
            if (model.Editor.EmotionMode == Dubbing.Core.Projects.EmotionMode.Vector)
            {
                var vector = model.Editor.EmotionVector.Split(',');
                if (vector.Length != 8 || vector.Any(v => !double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n)))
                    throw new InvalidOperationException(text["Emotion vector requires eight numbers."]);
            }
            Close(true);
        }
        catch (Exception ex) { Validation.Text = ex.Message; }
    }
    private async void PreviewClick(object? sender, RoutedEventArgs e)
    {
        if (previewing) return;
        previewing = true;
        try
        {
            if (model.Editor.StartSeconds < 0 || model.Editor.EndSeconds <= model.Editor.StartSeconds || model.Editor.EndSeconds > model.VideoDuration)
                throw new InvalidOperationException(text["Segment range must stay inside the video."]);
            await model.Preview(model.Editor.StartSeconds, model.Editor.EndSeconds);
        }
        catch (Exception ex) { Validation.Text = ex.Message; }
        finally { previewing = false; if (closed) model.Stop(); }
    }
}
