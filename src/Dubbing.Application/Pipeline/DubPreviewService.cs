using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

// A single video/audio media clock also keeps paused seeking and overlapping clips synchronized.
public sealed class DubPreviewService(IMediaEngine media)
{
    public async Task<string> PrepareAsync(DubbingProject project, string directory, IProgress<EngineProgress>? progress, CancellationToken ct, AudioAudition audition = AudioAudition.Mix, string? segmentId = null)
    {
        var source = project.Source ?? throw new InvalidOperationException("Import a video first.");
        var clips = await ProjectAudioMix.ClipsAsync(project, directory, false, progress, ct, audition, segmentId);
        var inputs = new List<object>();
        foreach (var clip in clips)
        {
            string fingerprint = await ContentFingerprint.ComputeAsync(clip.Path, ct);
            inputs.Add(new { clip.Path, clip.Start, clip.Gain, clip.Limit, fingerprint });
        }
        progress?.Report(new(0, $"Dub preview: {clips.Count} audio sources; mix mode {audition}."));
        var sourceFile = new FileInfo(source.Path);
        string key = StageCache.Key("dub-preview-v2", sourceFile.FullName, sourceFile.Length, sourceFile.LastWriteTimeUtc,
            source.Duration, source.SampleRate, ProjectAudioMix.Channels(project), inputs);
        string folder = Path.Combine(directory, "cache", "dub-preview");
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, key + ".mkv");
        if (File.Exists(output)) { progress?.Report(new(1, "Dub preview ready (cached).")); return output; }
        string scratch = Path.Combine(folder, "building-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string audio = Path.Combine(scratch, "dialogue.wav");
            await ProjectAudioMix.RenderAsync(media, clips, audio, source.Duration, source.SampleRate, ProjectAudioMix.Channels(project), progress, ct);
            string video = Path.Combine(scratch, "preview.mkv");
            // MKV copies the video stream and maps only the new audio track.
            await media.MuxAsync(source.Path, audio, video, progress, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(video, output, true);
            progress?.Report(new(1, "Dub preview ready."));
            return output;
        }
        finally { Directory.Delete(scratch, true); }
    }
}
