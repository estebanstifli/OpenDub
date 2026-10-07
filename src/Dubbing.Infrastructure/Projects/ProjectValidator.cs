using Dubbing.Core;
using Dubbing.Core.Projects;

namespace Dubbing.Infrastructure.Projects;

public static class ProjectValidator
{
    public static void Validate(DubbingProject project)
    {
        Require(project.SchemaVersion == DubbingProject.CurrentSchemaVersion, "Unsupported project version.");
        Require(!string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Name), "Missing project name or ID.");
        if (project.Settings is null || project.Engines is null || project.Segments is null || project.Speakers is null)
            throw new ProjectFormatException("Missing project data.");
        Require(!string.IsNullOrWhiteSpace(project.Settings.SourceLanguage) &&
            !string.IsNullOrWhiteSpace(project.Settings.TargetLanguage), "Missing languages.");
        Require(double.IsFinite(project.Settings.DurationTolerance) && project.Settings.DurationTolerance >= 0,
            "Invalid duration tolerance.");
        Require(double.IsFinite(project.Settings.VoiceGain) && project.Settings.VoiceGain >= 0 &&
            double.IsFinite(project.Settings.BackgroundGain) && project.Settings.BackgroundGain >= 0, "Invalid gain.");
        Require(project.Settings.Glossary is not null, "Invalid glossary.");
        Require(double.IsFinite(project.Settings.MaximumTimeStretch) && project.Settings.MaximumTimeStretch is >= 0 and <= 0.5, "Invalid time stretch limit.");
        Require(project.Settings.MaximumTtsRetries is >= 0 and <= 10 && project.Settings.MaximumTranslationRetries is >= 0 and <= 10, "Invalid retry limit.");
        Require(project.Settings.ExportContainer is "mp4" or "mkv", "Unsupported export container.");
        if (project.Stems is null || project.Metrics is null) throw new ProjectFormatException("Missing project metadata.");
        foreach (var stem in project.Stems.Values) Require(IsProjectPath(stem.Path), "Stems must be inside the project.");
        if (project.Source is { } source)
            Require(!string.IsNullOrWhiteSpace(source.Path) && !string.IsNullOrWhiteSpace(source.Fingerprint) &&
                source.Duration > TimeSpan.Zero && source.AudioChannels > 0 && source.SampleRate > 0, "Invalid media source.");
        var speakers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var speaker in project.Speakers)
        {
            if (speaker is null) throw new ProjectFormatException("Null speaker.");
            Require(!string.IsNullOrWhiteSpace(speaker.Id) && speakers.Add(speaker.Id), "Empty or duplicate speaker ID.");
        }
        var segments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in project.Segments)
        {
            if (segment is null) throw new ProjectFormatException("Null segment.");
            Require(!string.IsNullOrWhiteSpace(segment.Id) && segments.Add(segment.Id), "Empty or duplicate segment ID.");
            Require(segment.Start >= TimeSpan.Zero && segment.End > segment.Start, "Invalid segment timing.");
            Require(segment.SpeakerId is null || speakers.Contains(segment.SpeakerId), "Unknown segment speaker.");
            if (segment.Source is null || segment.Translation is null || segment.Tts is null ||
                segment.Emotion is null || segment.DirtyStages is null || segment.Words is null)
                throw new ProjectFormatException("Incomplete segment.");
            Require(segment.Source.Text is not null && segment.Translation.Text is not null &&
                !string.IsNullOrWhiteSpace(segment.Source.Language) && !string.IsNullOrWhiteSpace(segment.Translation.Language), "Invalid segment text.");
            Require(segment.Tts.EngineOptions is not null && (segment.Tts.SpeakingRate is null ||
                double.IsFinite(segment.Tts.SpeakingRate.Value) && segment.Tts.SpeakingRate > 0), "Invalid speech options.");
            Require(segment.Emotion.Intensity is null || double.IsFinite(segment.Emotion.Intensity.Value) &&
                segment.Emotion.Intensity is >= 0 and <= 1, "Invalid emotion intensity.");
            Require(segment.Emotion.Vector is null || segment.Emotion.Vector.All(v => double.IsFinite(v) && v >= 0), "Emotion vector must contain nonnegative finite values.");
            foreach (var speech in new[] { segment.GeneratedSpeech, segment.FittedSpeech }.OfType<GeneratedSpeech>())
            {
                Require(speech.Duration > TimeSpan.Zero, "Invalid speech duration.");
                Require(!string.IsNullOrWhiteSpace(speech.File) && !Path.IsPathRooted(speech.File) &&
                    !speech.File.Contains(':') && !speech.File.Split('/', '\\').Contains(".."), "Generated speech must be inside the project.");
            }
        }
    }

    private static bool IsProjectPath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':') && !path.Split('/', '\\').Contains("..");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ProjectFormatException(message);
    }
}
