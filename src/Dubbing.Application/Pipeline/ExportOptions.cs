namespace Dubbing.Application.Pipeline;

public sealed record ExportOptions
{
    public static IReadOnlyList<string> Profiles { get; } = ["Recommended MP4", "Preserve video quality (MKV)", "Editing package", "Audio only"];
    public string Profile { get; init; } = "Recommended MP4";
    public string? Directory { get; init; }
    public bool KeepOriginalAudio { get; init; }
    public bool EmbedSubtitles { get; init; } = true;
    public bool AllowDurationMismatch { get; init; }
    public static IReadOnlyList<string> AudioFormats { get; } = ["MP3", "WAV", "FLAC", "M4A", "OGG"];
    public string AudioFormat { get; init; } = "MP3";
    public TimeSpan? Start { get; init; }
    public TimeSpan? End { get; init; }
    public static TimeSpan ParseTime(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(\d{1,6}):([0-5]\d):([0-5]\d)(?:[.,](\d{1,7}))?$");
        if (!match.Success) throw new FormatException("Enter a time as HH:MM:SS, optionally with milliseconds.");
        return TimeSpan.FromHours(int.Parse(match.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(match.Groups[2].Value))
            + TimeSpan.FromSeconds(int.Parse(match.Groups[3].Value))
            + TimeSpan.FromTicks(match.Groups[4].Success ? int.Parse(match.Groups[4].Value.PadRight(7, '0')) : 0);
    }
    public (TimeSpan Start, TimeSpan End) Range(TimeSpan duration)
    {
        var start = Start ?? TimeSpan.Zero; var end = End ?? duration;
        if (start < TimeSpan.Zero || end <= start || end > duration) throw new ArgumentException("Choose a time range inside the video, with the end after the start.");
        return (start, end);
    }
}
