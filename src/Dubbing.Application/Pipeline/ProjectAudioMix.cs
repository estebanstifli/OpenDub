using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public enum AudioAudition { Mix, OriginalVoice, Dub, Background }
public static class ProjectAudioMix
{
    public static bool IsBackground(string key) => key is "instrumental" or "background" or "music" or "effects" || key.StartsWith("background-", StringComparison.Ordinal);
    public static int Channels(DubbingProject project) => Math.Clamp(Math.Max(project.Source?.AudioChannels ?? 2,
        project.AudioTracks.Where(t => t.Role is AudioTrackRole.Background or AudioTrackRole.Music or AudioTrackRole.Effects).Select(t => t.Channels).DefaultIfEmpty(1).Max()), 1, 8);

    public static async Task<IReadOnlyList<AudioClip>> ClipsAsync(DubbingProject project, string directory, bool strict,
        IProgress<EngineProgress>? progress, CancellationToken ct, AudioAudition audition = AudioAudition.Mix, string? segmentId = null)
    {
        if (project.AudioTracks.Count > 0 && project.SelectedSourceAudio is null)
            throw new InvalidOperationException("Audio track settings changed. Prepare audio before playback or export.");
        var clips = new List<AudioClip>();
        var settings = project.Settings;
        bool originalMix = false;
        if (audition == AudioAudition.Dub || audition == AudioAudition.Mix && !settings.DubMuted)
        foreach (var segment in project.Segments.Where(s => !s.ExcludedFromDubbing && (segmentId is null || s.Id == segmentId)))
        {
            var speech = !segment.DirtyStages.Contains(PipelineStage.DurationFit) ? segment.FittedSpeech ?? segment.GeneratedSpeech : segment.GeneratedSpeech;
            string? path = speech is null ? null : Path.Combine(directory, speech.File);
            if (speech is null || !File.Exists(path))
            {
                if (strict) throw new InvalidOperationException($"Generate speech for {segment.Id}.");
                progress?.Report(new(0, $"Dub preview: segment {segment.Id} has no generated speech; leaving it silent.")); continue;
            }
            if (strict)
            {
                if (segment.DirtyStages.Any(s => s is not PipelineStage.Mix and not PipelineStage.Mux and not PipelineStage.DurationFit))
                    throw new InvalidOperationException($"Regenerate speech for {segment.Id}.");
                if (speech.Fingerprint is null || await ContentFingerprint.ComputeAsync(path!, ct) != speech.Fingerprint)
                    throw new InvalidOperationException($"Audio for {segment.Id} has changed. Regenerate speech.");
                if (Math.Abs(speech.Duration.TotalSeconds / segment.TargetDuration.TotalSeconds - 1) > settings.DurationTolerance)
                    throw new InvalidOperationException($"Speech for {segment.Id} does not fit its time slot. Review the translation and timing.");
            }
            clips.Add(new(path!, segment.Start, settings.VoiceGain));
        }
        if (audition == AudioAudition.OriginalVoice || audition == AudioAudition.Mix && !settings.OriginalVoiceMuted)
        {
            if (project.Stems.TryGetValue("vocals", out var vocals)) clips.Add(new(Path.Combine(directory, vocals.Path), TimeSpan.Zero, settings.OriginalVoiceGain));
            else
            {
                string? source = project.SelectedSourceAudio is { } selected ? Path.Combine(directory, selected.Path) : project.Source?.Path;
                if (source is null) throw new InvalidOperationException("Import a video first.");
                clips.Add(new(source, TimeSpan.Zero, settings.OriginalVoiceGain));
                originalMix = true;
                progress?.Report(new(0, "Original track contains the full mix. Additional background is omitted to avoid doubling music and effects."));
            }
        }
        if (audition == AudioAudition.Background || audition == AudioAudition.Mix && !settings.BackgroundMuted && !originalMix)
        {
            var backgrounds = project.Stems.Where(s => IsBackground(s.Key)).ToArray();
            if (backgrounds.Length == 0)
            {
                if (audition == AudioAudition.Background) throw new InvalidOperationException("No clean background is available. Prepare audio first.");
                progress?.Report(new(0, "No clean background available; playing generated dialogue without the original mix."));
            }
            foreach (var stem in backgrounds) clips.Add(new(Path.Combine(directory, stem.Value.Path), TimeSpan.Zero, settings.BackgroundGain));
        }
        return clips;
    }

    public static async Task RenderAsync(IMediaEngine media, IReadOnlyList<AudioClip> input, string output, TimeSpan duration,
        int sampleRate, int channels, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        string scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var clips = input.ToList(); int level = 0;
            while (clips.Count > 24)
            {
                var next = new List<AudioClip>();
                foreach (var batch in clips.Chunk(24))
                {
                    string partial = Path.Combine(scratch, $"{level}-{next.Count}.wav");
                    await media.MixAsync(new(batch, partial, duration, sampleRate, channels, ProtectPeaks: false), progress, ct);
                    next.Add(new(partial, TimeSpan.Zero));
                }
                clips = next; level++;
            }
            await media.MixAsync(new(clips, output, duration, sampleRate, channels), progress, ct);
        }
        finally { Directory.Delete(scratch, true); }
    }
}
