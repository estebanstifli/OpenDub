using CommunityToolkit.Mvvm.ComponentModel;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public partial class SegmentEditorViewModel : ObservableObject
{
    [ObservableProperty] private bool excludedFromDubbing;
    [ObservableProperty] private bool clipAudioToSlot;
    [ObservableProperty] private string sourceText = "";
    [ObservableProperty] private string translationText = "";
    [ObservableProperty] private double startSeconds;
    [ObservableProperty] private double endSeconds;
    [ObservableProperty] private SpeakerProfile? speaker;
    [ObservableProperty] private EngineDescriptor? voiceEngine;
    [ObservableProperty] private EmotionMode emotionMode;
    [ObservableProperty] private string emotionText = "";
    [ObservableProperty] private double intensity = 0.75;
    [ObservableProperty] private double speed = 1;
    [ObservableProperty] private string seed = "";
    [ObservableProperty] private string advancedOptions = "{}";
    [ObservableProperty] private string emotionReference = "";
    [ObservableProperty] private string emotionVector = "0,0,0,0,0,0,0,0";
    [ObservableProperty] private string durationSummary = "";
    public void CopyFrom(SegmentEditorViewModel other)
    {
        VoiceEngine = other.VoiceEngine;
        ExcludedFromDubbing = other.ExcludedFromDubbing;
        ClipAudioToSlot = other.ClipAudioToSlot;
        SourceText = other.SourceText;
        TranslationText = other.TranslationText;
        StartSeconds = other.StartSeconds;
        EndSeconds = other.EndSeconds;
        Speaker = other.Speaker;
        EmotionMode = other.EmotionMode;
        EmotionText = other.EmotionText;
        Intensity = other.Intensity;
        Speed = other.Speed;
        Seed = other.Seed;
        AdvancedOptions = other.AdvancedOptions;
        EmotionReference = other.EmotionReference;
        EmotionVector = other.EmotionVector;
        DurationSummary = other.DurationSummary;
    }
    public bool SupportsEmotion => VoiceEngine?.Capabilities is { } c && (c.SupportsEmotionReferenceAudio || c.SupportsEmotionLabels || c.SupportsEmotionInstruction || c.SupportsEmotionVector);
    public bool SupportsSpeed => VoiceEngine?.Capabilities.SupportsSpeakingRate == true;
    public bool SupportsSeed => VoiceEngine?.Capabilities.SupportsSeed == true;
    public bool SupportsVoiceReference => VoiceEngine?.Capabilities.SupportsVoiceCloning == true;
    public IReadOnlyList<EmotionMode> EmotionModes => Enum.GetValues<EmotionMode>().Where(m => m switch
    {
        EmotionMode.None => true,
        EmotionMode.SourceAudio or EmotionMode.ReferenceAudio => VoiceEngine?.Capabilities.SupportsEmotionReferenceAudio == true,
        EmotionMode.Label => VoiceEngine?.Capabilities.SupportsEmotionLabels == true,
        EmotionMode.Vector => VoiceEngine?.Capabilities.SupportsEmotionVector == true,
        EmotionMode.Instruction => VoiceEngine?.Capabilities.SupportsEmotionInstruction == true,
        _ => false
    }).ToArray();
    partial void OnVoiceEngineChanged(EngineDescriptor? value)
    {
        OnPropertyChanged(nameof(SupportsEmotion)); OnPropertyChanged(nameof(SupportsSpeed));
        OnPropertyChanged(nameof(SupportsSeed)); OnPropertyChanged(nameof(SupportsVoiceReference)); OnPropertyChanged(nameof(EmotionModes));
        if (!EmotionModes.Contains(EmotionMode)) EmotionMode = EmotionMode.None;
    }
}
