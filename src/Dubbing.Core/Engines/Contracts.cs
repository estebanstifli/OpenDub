using Dubbing.Core.Projects;

namespace Dubbing.Core.Engines;

public sealed record AudioReference(string Path, string? Fingerprint = null,
    TimeSpan? Start = null, TimeSpan? End = null);
public sealed record WordTiming(string Text, TimeSpan Start, TimeSpan End, float? Confidence = null);
public sealed record TranscriptSegment(string Text, TimeSpan Start, TimeSpan End,
    IReadOnlyList<WordTiming>? Words = null, float? Confidence = null);
public sealed record TranscriptionRequest(AudioReference Audio, string? Language = null, TimeSpan? Duration = null);
public sealed record TranscriptionResult(string Text, string Language, IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<WordTiming>? Words = null, float? Confidence = null);
public interface ITranscriptionEngine : IEngine
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
}

public sealed record SpeakerTurn(TimeSpan Start, TimeSpan End, string SpeakerId, float? Confidence = null);
public sealed record DiarizationRequest(AudioReference Audio, TimeSpan? Duration = null, int? SpeakerCount = null);
public sealed record DiarizationResult(IReadOnlyList<SpeakerTurn> Turns);
public interface IDiarizationEngine : IEngine
{
    Task<DiarizationResult> DiarizeAsync(DiarizationRequest request,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
}

public sealed record AlignmentRequest(AudioReference Audio, string Text, string Language);
public sealed record AlignmentResult(IReadOnlyList<WordTiming> Words);
public interface IAlignmentEngine : IEngine
{
    Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken cancellationToken);
}

public sealed record SeparationRequest(AudioReference Audio, string OutputDirectory);
public sealed record SeparationResult(IReadOnlyDictionary<string, AudioReference> Stems);
public interface ISeparationEngine : IEngine
{
    Task<SeparationResult> SeparateAsync(SeparationRequest request,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
}

public sealed record TranslationRequest(string SegmentId, string Text, string SourceLanguage, string TargetLanguage)
{
    public string? ModelId { get; init; }
    public IReadOnlyList<string> PreviousSegments { get; init; } = [];
    public IReadOnlyList<string> NextSegments { get; init; } = [];
    public string? SpeakerId { get; init; }
    public TimeSpan? TargetDuration { get; init; }
    public IReadOnlyDictionary<string, string> Glossary { get; init; } = new Dictionary<string, string>();
    public string? StyleInstructions { get; init; }
}
public sealed record TranslationResult(string SegmentId, string Text, string Language);
public interface ITranslationEngine : IEngine
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public sealed record TtsRequest
{
    public required string Text { get; init; }
    public required string Language { get; init; }
    public required string OutputPath { get; init; }
    public string? SpeakerProfileId { get; init; }
    public AudioReference? VoiceReference { get; init; }
    public EmotionDescriptor? Emotion { get; init; }
    public TimeSpan? TargetDuration { get; init; }
    public double? SpeakingRate { get; init; }
    public ulong? Seed { get; init; }
    public IReadOnlyDictionary<string, object?> EngineOptions { get; init; } = new Dictionary<string, object?>();
}
public sealed record TtsResult(AudioReference Audio, TimeSpan Duration, int SampleRate, int Channels);
public interface ITtsEngine : IEngine
{
    Task<TtsResult> SynthesizeAsync(TtsRequest request,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
}

public sealed record ModelDescriptor(string Id, string ProviderId, string DisplayName, string? Version,
    string? License, Uri? Source, bool Installed, long? SizeBytes = null);
public interface IModelManager
{
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken);
    Task InstallAsync(string modelId, IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
    Task RemoveAsync(string modelId, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken);
}
