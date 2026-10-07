using CommunityToolkit.Mvvm.ComponentModel;
using Dubbing.Core.Projects;
using Dubbing.App.Localization;

namespace Dubbing.App.ViewModels;

public partial class SegmentViewModel : ObservableObject
{
    private readonly TextCatalog text;
    public string Id { get; }
    [ObservableProperty] private string timing;
    [ObservableProperty] private string speaker;
    [ObservableProperty] private string sourceText = "";
    [ObservableProperty] private bool isActive;
    public int Number { get; init; }
    public event Action<SegmentViewModel>? SourceChanged;
    [ObservableProperty] private string translationText;
    [ObservableProperty] private string state;
    public event Action<SegmentViewModel>? TranslationChanged;
    public SegmentViewModel(DubSegment segment, TextCatalog? text = null, string? speakerName = null)
    {
        this.text = text ?? new TextCatalog();
        Id = segment.Id; Timing = $"{segment.Start:hh\\:mm\\:ss\\.fff} → {segment.End:hh\\:mm\\:ss\\.fff}";
        Speaker = speakerName ?? segment.SpeakerId ?? this.text["Unknown speaker"]; sourceText = segment.Source.Text;
        translationText = segment.Translation.Text;
        state = segment.Translation.Locked ? this.text["Manual edit · locked"] : this.text[segment.Status.ToString()];
        if (segment.ExcludedFromDubbing) state = this.text["Excluded from dubbing"];
        if (segment.ExcludedFromDubbing && segment.ExclusionReason is { } reason) state += " · " + this.text[reason];
    }
    partial void OnSourceTextChanged(string value) => SourceChanged?.Invoke(this);
    partial void OnTranslationTextChanged(string value)
    {
        State = text["Manual edit · locked"];
        TranslationChanged?.Invoke(this);
    }
}
