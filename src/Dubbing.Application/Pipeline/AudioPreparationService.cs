using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class AudioPreparationService(IMediaEngine media, IEngineRegistry registry)
{
    public static IReadOnlyList<SourceAudioTrack> Discover(MediaInfo info, IReadOnlyList<SourceAudioTrack>? saved = null)
    {
        var audio = info.Streams.Where(s => s.Type == "audio").ToArray();
        int? preferred = (audio.FirstOrDefault(s => s.IsDefault) ?? audio.FirstOrDefault())?.Index;
        return audio.Select(s => new SourceAudioTrack(s.Index, s.Codec, s.Channels ?? 1, s.SampleRate ?? 48000,
            s.Language, s.Title, s.ChannelLayout, s.IsDefault,
            saved?.FirstOrDefault(t => t.Index == s.Index)?.Role ?? (s.Index == preferred ? AudioTrackRole.Mixed : AudioTrackRole.Ignore))).ToArray();
    }

    public async Task<DubbingProject> PrepareAsync(DubbingProject project, string directory, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var source = project.Source ?? throw new InvalidOperationException("Import a video first.");
        var info = await media.ProbeAsync(source.Path, ct);
        string fingerprint = await ContentFingerprint.ComputeAsync(source.Path, ct);
        if (fingerprint != source.Fingerprint) throw new InvalidOperationException("The original video has changed since import.");
        var tracks = Discover(info, project.AudioTracks);
        int selected = project.Settings.AnalysisStreamIndex ?? tracks.FirstOrDefault(t => t.Role is AudioTrackRole.Mixed or AudioTrackRole.Dialogue)?.Index
            ?? throw new InvalidOperationException("Choose a dialogue or mixed track for analysis.");
        var analysis = tracks.SingleOrDefault(t => t.Index == selected && t.Role is AudioTrackRole.Mixed or AudioTrackRole.Dialogue)
            ?? throw new InvalidOperationException("The analysis track must be classified as Mixed or Dialogue.");
        if (tracks.Any(t => t.Index != selected && t.Role is AudioTrackRole.Mixed or AudioTrackRole.Dialogue))
            throw new InvalidOperationException("Choose one dialogue or mixed track; mark alternate language tracks as Ignore.");
        ISeparationEngine? separator = analysis.Role == AudioTrackRole.Mixed && project.Settings.UseSeparation
            ? registry.GetEngine<ISeparationEngine>(project.Engines.Separation ?? throw new InvalidOperationException("Select a separation engine.")) : null;
        string key = StageCache.Key("audio-tracks-v1", fingerprint, tracks, selected, separator?.Descriptor.EngineId, separator?.Descriptor.Version);
        string folder = Path.Combine(directory, "assets", "audio", key);
        Directory.CreateDirectory(folder);
        async Task CachedAudio(string path, Func<string, Task> render)
        {
            if (File.Exists(path)) return;
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".wav";
            try { await render(pending); ct.ThrowIfCancellationRequested(); File.Move(pending, path, true); }
            finally { File.Delete(pending); }
        }
        async Task<string> Extract(SourceAudioTrack track)
        {
            string path = Path.Combine(folder, $"track-{track.Index}.wav");
            await CachedAudio(path, pending => media.ExtractTrackAsync(source.Path, track.Index, pending, track.SampleRate, track.Channels, progress, ct));
            return path;
        }
        string original = await Extract(analysis);
        string? vocals = analysis.Role == AudioTrackRole.Dialogue ? original : null;
        string? separatedBackground = null;
        if (separator is not null)
        {
            progress?.Report(new(0, "Separating dialogue from background. Existing segments and translations are preserved."));
            if (analysis.Channels > 2) progress?.Report(new(0, "AI separation uses a stereo downmix; it cannot preserve the original surround separation."));
            string input = Path.Combine(folder, "separation.wav"), stemsFolder = Path.Combine(folder, "separated");
            await CachedAudio(input, pending => media.ExtractAudioAsync(original, pending, 44100, 2, progress, ct));
            string marker = Path.Combine(stemsFolder, "complete");
            if (File.Exists(marker) && File.Exists(Path.Combine(stemsFolder, "vocals.wav")) && File.Exists(Path.Combine(stemsFolder, "instrumental.wav")))
            { vocals = Path.Combine(stemsFolder, "vocals.wav"); separatedBackground = Path.Combine(stemsFolder, "instrumental.wav"); }
            else
            {
                var result = await separator.SeparateAsync(new(new(input), stemsFolder), progress, ct);
                if (!result.Stems.TryGetValue("vocals", out var voice) || !result.Stems.TryGetValue("instrumental", out var background)
                    || !File.Exists(voice.Path) || !File.Exists(background.Path))
                    throw new InvalidOperationException("Separation did not produce both vocals and instrumental audio.");
                vocals = voice.Path; separatedBackground = background.Path;
                await File.WriteAllTextAsync(marker, key, ct);
            }
        }
        var stems = new Dictionary<string, AudioReference>();
        AudioReference Reference(string path) => new(Path.GetRelativePath(directory, path));
        if (vocals is not null) stems["vocals"] = Reference(vocals);
        var backgroundTracks = tracks.Where(t => t.Role is AudioTrackRole.Background or AudioTrackRole.Music or AudioTrackRole.Effects).ToArray();
        // Explicit M&E/music/effects replace the estimated residual, never double it.
        foreach (var track in backgroundTracks) stems[$"background-{track.Index}"] = Reference(await Extract(track));
        if (backgroundTracks.Length == 0 && separatedBackground is not null) stems["instrumental"] = Reference(separatedBackground);
        string analysisPath = Path.Combine(folder, "analysis.wav");
        await CachedAudio(analysisPath, pending => media.ExtractAudioAsync(vocals ?? original, pending, 16000, 1, progress, ct));
        progress?.Report(new(1, stems.Keys.Any(ProjectAudioMix.IsBackground)
            ? "Audio ready: original dialogue and background can be mixed independently."
            : "No clean background available. Prepare separation or assign a music/effects track; the original mix will not be used as background."));
        return project with { AudioTracks = tracks, SelectedSourceAudio = Reference(original), AnalysisAudio = Reference(analysisPath), Stems = stems,
            Source = source with { AudioChannels = analysis.Channels, SampleRate = analysis.SampleRate },
            Settings = project.Settings with { AnalysisStreamIndex = selected } };
    }
}
