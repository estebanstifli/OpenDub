namespace Dubbing.Application.Pipeline;

public sealed record ExportOptions
{
    public static IReadOnlyList<string> Profiles { get; } = ["Recommended MP4", "Preserve video quality (MKV)", "Editing package", "Audio only"];
    public string Profile { get; init; } = "Recommended MP4";
    public string? Directory { get; init; }
    public bool KeepOriginalAudio { get; init; }
    public bool EmbedSubtitles { get; init; } = true;
    public bool AllowDurationMismatch { get; init; }
}
