namespace Dubbing.Core.Engines;

public enum EngineTask { Transcription, Diarization, Alignment, Translation, Tts, Separation }

public sealed record EngineCapabilities
{
    public bool SupportsStreaming { get; init; }
    public bool SupportsWordTimestamps { get; init; }
    public bool SupportsSpeakerLabels { get; init; }
    public bool SupportsVoiceCloning { get; init; }
    public bool SupportsEmotionReferenceAudio { get; init; }
    public bool SupportsEmotionLabels { get; init; }
    public bool SupportsEmotionVector { get; init; }
    public bool SupportsEmotionInstruction { get; init; }
    public bool SupportsNonVerbalTags { get; init; }
    public bool SupportsSpeakingRate { get; init; }
    public bool SupportsTargetDuration { get; init; }
    public bool SupportsSeed { get; init; }
    public int? MaxSpeakers { get; init; }
}

// Runtime backend names are provider metadata, never Core dependencies.
public sealed record HardwareRequirements(long MinimumRamBytes = 0, long? MinimumAcceleratorMemoryBytes = null);

public sealed record EngineDescriptor(
    string ProviderId, string EngineId, string DisplayName, EngineTask Task,
    IReadOnlySet<string> Languages, EngineCapabilities Capabilities,
    HardwareRequirements Hardware, string? License = null, string? Version = null);

public sealed record EngineProgress(double Fraction, string Message, string? SegmentId = null);

public interface IEngine { EngineDescriptor Descriptor { get; } }

public interface IEngineRegistry
{
    IReadOnlyList<EngineDescriptor> GetAvailableEngines(EngineTask task);
    T GetEngine<T>(string engineId) where T : class, IEngine;
}

public enum EngineOptionType { Boolean, Integer, Number, Text, Choice }
public sealed record EngineOptionDescriptor(string Key, string DisplayName, EngineOptionType Type,
    object? DefaultValue = null, double? Min = null, double? Max = null, IReadOnlyList<string>? Values = null);
