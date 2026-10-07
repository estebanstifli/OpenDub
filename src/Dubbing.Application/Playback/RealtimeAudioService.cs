using Dubbing.Application.Media;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Playback;

public sealed record RealtimePlaybackPlan(string Video, TimeSpan Duration, IReadOnlyList<AudioClip> Clips);

public sealed class RealtimeAudioService(IMediaEngine media)
{
    private readonly Dictionary<string, (string Path, TimeSpan Duration)> prepared = [];
    private readonly SemaphoreSlim preparation = new(1, 1);
    public async Task<RealtimePlaybackPlan> PrepareAsync(DubbingProject project, string directory, IProgress<EngineProgress>? progress,
        CancellationToken ct, AudioAudition audition = AudioAudition.Mix)
    {
        await preparation.WaitAsync(ct);
        try
        {
        var source = project.Source ?? throw new InvalidOperationException("Import a video first.");
        var input = await ProjectAudioMix.ClipsAsync(project, directory, false, progress, ct, audition);
        var clips = new List<AudioClip>(); int converted = 0;
        foreach (var clip in input)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(clip.Path);
            string key = StageCache.Key(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
            if (!prepared.TryGetValue(key, out var cached))
            {
                string path = clip.Path;
                try { using var wave = new PcmWaveSource(path); cached = (path, TimeSpan.FromSeconds((double)wave.Frames / wave.SampleRate)); }
                catch (NotSupportedException)
                {
                    path = Path.Combine(directory, "cache", "pcm-clips", key + ".wav");
                    if (!File.Exists(path)) { await media.ExtractAudioAsync(clip.Path, path, 48000, 2, progress, ct); converted++; }
                    using var wave = new PcmWaveSource(path); cached = (path, TimeSpan.FromSeconds((double)wave.Frames / wave.SampleRate));
                }
                if (prepared.Count >= 4096) prepared.Clear();
                prepared[key] = cached;
            }
            clips.Add(clip with { Path = cached.Path, Limit = clip.Limit is { } limit && limit < cached.Duration ? limit : cached.Duration });
        }
        progress?.Report(new(1, $"Realtime playback ready · {clips.Count} clips · {converted} conversions · no full-film render."));
        return new(source.Path, source.Duration, clips);
        }
        finally { preparation.Release(); }
    }
}
