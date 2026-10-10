using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using System.Text.Json.Serialization;

namespace Dubbing.Core.Projects;

public enum EmotionMode { None, SourceAudio, ReferenceAudio, Label, Vector, Instruction }
public sealed record EmotionDescriptor
{
    public EmotionMode Mode { get; init; }
    public string? Label { get; init; }
    public double? Intensity { get; init; }
    public AudioReference? ReferenceAudio { get; init; }
    public IReadOnlyList<double>? Vector { get; init; }
    public string? Instruction { get; init; }
}

public sealed record LocalizedText(string Language, string Text, bool Locked = false);
public enum SegmentStatus { Analyzed, Translated, TranslationEdited, TtsPending, TtsGenerated, TtsDirty, Ready, Error }
public sealed record SpeechSettings
{
    public string? EngineId { get; init; }
    public string? ModelId { get; init; }
    public string? ModelVersion { get; init; }
    public double? SpeakingRate { get; init; }
    public ulong? Seed { get; init; }
    public IReadOnlyDictionary<string, object?> EngineOptions { get; init; } = new Dictionary<string, object?>();
}
public sealed record GeneratedSpeech(string File, TimeSpan Duration, string CacheKey, string? Fingerprint = null);
public sealed record JobMetric(string Stage, double WallSeconds, double? AudioSeconds = null, string? SegmentId = null, int Retries = 0);
public sealed record DubSegment
{
    public bool ClipAudioToSlot { get; init; }
    public TimeSpan? SourceReferenceStart { get; init; }
    public TimeSpan? SourceReferenceEnd { get; init; }
    public bool ExcludedFromDubbing { get; init; }
    public string? ExclusionReason { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string? SpeakerId { get; init; }
    public LocalizedText Source { get; init; } = new("en", "");
    public LocalizedText Translation { get; init; } = new("es", "");
    public IReadOnlyList<WordTiming> Words { get; init; } = [];
    public EmotionDescriptor Emotion { get; init; } = new();
    public SpeechSettings Tts { get; init; } = new();
    public GeneratedSpeech? GeneratedSpeech { get; init; }
    public GeneratedSpeech? FittedSpeech { get; init; }
    public SegmentStatus Status { get; init; } = SegmentStatus.Analyzed;
    public IReadOnlyList<PipelineStage> DirtyStages { get; init; } = [PipelineStage.Translation, PipelineStage.Tts,
        PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux];
    [JsonIgnore] public TimeSpan TargetDuration => End - Start;
}
public sealed record SpeakerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; init; } = "Speaker";
    public string Color { get; init; } = "#64D8CB";
    public AudioReference? VoiceReference { get; init; }
    // Null supports older projects: automatic references had fingerprints, imported references did not.
    public bool? VoiceReferenceIsAutomatic { get; init; }
    public string? VoiceReferenceSegmentId { get; init; }
    public double? VoiceReferenceSourceStart { get; init; }
    public double? VoiceReferenceSourceEnd { get; init; }
    [JsonIgnore] public bool HasManualVoiceReference => VoiceReference is not null &&
        (VoiceReferenceIsAutomatic == false || (VoiceReferenceIsAutomatic is null && VoiceReference.Fingerprint is null));
    public string? PreferredTtsEngine { get; init; }
    public string Notes { get; init; } = "";
}
public sealed record EngineSelection
{
    public string? Transcription { get; init; }
    public string? Diarization { get; init; }
    public string? Alignment { get; init; }
    public string? Translation { get; init; }
    public string? Tts { get; init; }
    public string? Separation { get; init; }
}
public sealed record ProjectSettings
{
    public int? AnalysisStreamIndex { get; init; }
    public bool OriginalVoiceMuted { get; init; } = true;
    public bool DubMuted { get; init; }
    public bool BackgroundMuted { get; init; }
    public double OriginalVoiceGain { get; init; } = 1;
    public bool NormalizeSegments { get; init; } = true;
    public bool ParagraphSegments { get; init; }
    public string? TranslationModelId { get; init; }
    public string TranslationPrompt { get; init; } = Dubbing.Core.Engines.TranslationPrompts.Default;
    public bool UseSourceVoicePerSegment { get; init; } = true;
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "es";
    public bool UseSeparation { get; init; }
    public bool UseAlignment { get; init; }
    public bool AutomaticVoiceReferences { get; init; } = true;
    public bool AutomaticEmotion { get; init; } = true;
    public double MaximumTimeStretch { get; init; } = 0.05;
    public int MaximumTtsRetries { get; init; } = 2;
    public int MaximumTranslationRetries { get; init; } = 2;
    public string ExportContainer { get; init; } = "mkv";
    public double DurationTolerance { get; init; } = 0.05;
    public double VoiceGain { get; init; } = 1;
    public double BackgroundGain { get; init; } = 1;
    public IReadOnlyDictionary<string, string> Glossary { get; init; } = new Dictionary<string, string>();
}
public sealed record SourceMedia(string Path, string Fingerprint, TimeSpan Duration, int AudioChannels, int SampleRate);
public enum AudioTrackRole { Mixed, Dialogue, Background, Music, Effects, Ignore }
public sealed record SourceAudioTrack(int Index, string Codec, int Channels, int SampleRate,
    string? Language = null, string? Title = null, string? ChannelLayout = null, bool IsDefault = false,
    AudioTrackRole Role = AudioTrackRole.Ignore);
public sealed record DubbingProject
{
    public string? LastExportFile { get; init; }
    public IReadOnlyList<string> AcceptedOverlaps { get; init; } = [];
    public IReadOnlyList<SourceAudioTrack> AudioTracks { get; init; } = [];
    public AudioReference? SelectedSourceAudio { get; init; }
    public AudioReference? AnalysisAudio { get; init; }
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Untitled project";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public SourceMedia? Source { get; init; }
    public EngineSelection Engines { get; init; } = new();
    public ProjectSettings Settings { get; init; } = new();
    public IReadOnlyList<SpeakerProfile> Speakers { get; init; } = [];
    public IReadOnlyList<DubSegment> Segments { get; init; } = [];
    public IReadOnlyDictionary<string, AudioReference> Stems { get; init; } = new Dictionary<string, AudioReference>();
    public IReadOnlyList<JobMetric> Metrics { get; init; } = [];
}

public interface IProjectStore
{
    Task<DubbingProject> LoadAsync(string directory, CancellationToken cancellationToken);
    Task SaveAsync(DubbingProject project, string directory, CancellationToken cancellationToken);
}
