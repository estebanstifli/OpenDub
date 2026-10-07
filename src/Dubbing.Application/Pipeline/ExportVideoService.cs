using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class ExportVideoService(IMediaEngine media, DubRenderService renderer, IProjectStore store, IEngineRegistry? registry = null, IExecutionMetadata? metadata = null)
{
    public async Task<DubbingProject> ExportAsync(DubbingProject project, string directory,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken, ExportOptions? options = null)
    {
        var source = project.Source ?? throw new InvalidOperationException("Import a video first.");
        if (await ContentFingerprint.ComputeAsync(source.Path, cancellationToken) != source.Fingerprint)
            throw new InvalidOperationException("The original video has changed since import.");
        if (options is not null && !ExportOptions.Profiles.Contains(options.Profile)) throw new ArgumentException("Unknown export profile.");
        string destination = options?.Directory ?? Path.Combine(directory, "renders");
        destination = Path.GetFullPath(destination);
        if (options is not null && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty export folder to preserve previous exports.");
        var audio = await renderer.RenderAsync(project, directory, progress, cancellationToken);
        string finalDestination = destination;
        if (options is not null) destination = Path.Combine(directory, "renders", "export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
        string subtitles = Path.Combine(destination, "subtitles.srt");
        await SubtitleExporter.WriteSrtAsync(project, subtitles, cancellationToken);
        string container = options is null ? project.Settings.ExportContainer : options.Profile == "Recommended MP4" ? "mp4" : "mkv";
        string output = Path.Combine(destination, "dubbed." + container);
        if (options?.Profile is not ("Audio only" or "Editing package"))
            await media.MuxAsync(source.Path, audio, output, new(CopyVideo: container == "mkv", KeepOriginalAudio: options?.KeepOriginalAudio == true,
                Subtitles: options?.EmbedSubtitles == true ? subtitles : null, Language: project.Settings.TargetLanguage), progress, cancellationToken);
        if (Path.GetFullPath(audio) != Path.Combine(destination, "dub_track.wav")) File.Copy(audio, Path.Combine(destination, "dub_track.wav"), true);
        if (options?.Profile == "Editing package")
        {
            foreach (var (name, audition) in new[] { ("dialogue.wav", AudioAudition.Dub), ("background.wav", AudioAudition.Background) })
            {
                if (audition == AudioAudition.Background && !project.Stems.Keys.Any(ProjectAudioMix.IsBackground)) continue;
                var clips = await ProjectAudioMix.ClipsAsync(project, directory, true, progress, cancellationToken, audition);
                await ProjectAudioMix.RenderAsync(media, clips, Path.Combine(destination, name), source.Duration, source.SampleRate, ProjectAudioMix.Channels(project), progress, cancellationToken);
            }
        }
        project = project with
        {
            Segments = project.Segments.Select(s => s.ExcludedFromDubbing ? s : s with { Status = SegmentStatus.Ready,
                DirtyStages = s.DirtyStages.Except([PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux]).ToArray() }).ToArray()
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "render-metadata.json"), System.Text.Json.JsonSerializer.Serialize(new
        { schemaVersion = 1, applicationVersion = typeof(ExportVideoService).Assembly.GetName().Version?.ToString(), project.Engines, project.Settings, project.Metrics,
            execution = metadata?.Describe(), availableEngineVersions = registry is null ? null : Enum.GetValues<EngineTask>().SelectMany(registry.GetAvailableEngines).Select(e => new { e.EngineId, e.Version }),
            segments = project.Segments.Select(s => new { s.Id, s.ExcludedFromDubbing, s.Tts, s.GeneratedSpeech, s.FittedSpeech }) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (options is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalDestination)!);
            if (Directory.Exists(finalDestination)) Directory.Delete(finalDestination, false);
            // Copy to a sibling staging folder so publication works across volumes.
            string staging = finalDestination + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                foreach (string file in Directory.EnumerateFiles(destination))
                {
                    await using var sourceFile = File.OpenRead(file);
                    await using var targetFile = new FileStream(Path.Combine(staging, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await sourceFile.CopyToAsync(targetFile, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested(); Directory.Move(staging, finalDestination);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        await store.SaveAsync(project, directory, cancellationToken);
        progress?.Report(new(1, "Export complete."));
        return project;
        }
        finally { if (options is not null && Directory.Exists(destination)) Directory.Delete(destination, true); }
    }
}
