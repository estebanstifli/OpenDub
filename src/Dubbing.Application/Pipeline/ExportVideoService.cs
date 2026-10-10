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
        var range = (options ?? new ExportOptions()).Range(source.Duration);
        bool partial = range.Start != TimeSpan.Zero || range.End != source.Duration;
        if (options?.Profile == "Audio only" && !ExportOptions.AudioFormats.Contains(options.AudioFormat)) throw new ArgumentException("Unknown audio format.");
        progress?.Report(new(0, "Export · validating source video and destination…"));
        if (await ContentFingerprint.ComputeAsync(source.Path, cancellationToken) != source.Fingerprint)
            throw new InvalidOperationException("The original video has changed since import.");
        if (options is not null && !ExportOptions.Profiles.Contains(options.Profile)) throw new ArgumentException("Unknown export profile.");
        string destination = options?.Directory ?? Path.Combine(directory, "renders");
        destination = Path.GetFullPath(destination);
        if (options is not null && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty export folder to preserve previous exports.");
        progress?.Report(new(.05, "Export · validating segments and mixing audio…"));
        var audio = await renderer.RenderAsync(project, directory, new PhaseProgress(progress, .05, .25, "Export audio"), cancellationToken, options?.AllowDurationMismatch ?? new ExportOptions().AllowDurationMismatch, range.Start, range.End);
        string finalDestination = destination;
        if (options is not null) destination = Path.Combine(directory, "renders", "export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
        string subtitles = Path.Combine(destination, "subtitles.srt");
        if (options?.Profile != "Audio only") await SubtitleExporter.WriteSrtAsync(project, subtitles, cancellationToken, range.Start, range.End);
        string container = options is null ? project.Settings.ExportContainer : options.Profile == "Recommended MP4" ? "mp4" : "mkv";
        string output = Path.Combine(destination, "dubbed." + container);
        if (options?.Profile is not ("Audio only" or "Editing package"))
        {
            progress?.Report(new(.30, container == "mp4" || partial ? "Export · encoding selected video (H.264/AAC)…" : "Export · copying video into MKV…"));
            await media.MuxAsync(source.Path, audio, output, new(CopyVideo: container == "mkv" && !partial, KeepOriginalAudio: options?.KeepOriginalAudio == true,
                Subtitles: options?.EmbedSubtitles == true ? subtitles : null, Language: project.Settings.TargetLanguage, Start: range.Start, Duration: range.End - range.Start), new PhaseProgress(progress, .30, .55, "Export video"), cancellationToken);
        }
        if (options?.Profile == "Audio only")
            await media.EncodeAudioAsync(audio, Path.Combine(destination, "dub_track." + options.AudioFormat.ToLowerInvariant()), options.AudioFormat,
                new PhaseProgress(progress, .30, .55, "Export " + options.AudioFormat), cancellationToken);
        else if (Path.GetFullPath(audio) != Path.Combine(destination, "dub_track.wav")) File.Copy(audio, Path.Combine(destination, "dub_track.wav"), true);
        if (options?.Profile == "Editing package")
        {
            int stemNumber = 0;
            foreach (var (name, audition) in new[] { ("dialogue.wav", AudioAudition.Dub), ("background.wav", AudioAudition.Background) })
            {
                if (audition == AudioAudition.Background && !project.Stems.Keys.Any(ProjectAudioMix.IsBackground)) continue;
                var stemProgress = new PhaseProgress(progress, .30 + stemNumber++ * .275, .275, "Export " + name);
                var clips = await ProjectAudioMix.ClipsAsync(project, directory, true, stemProgress, cancellationToken, audition, allowDurationMismatch: options.AllowDurationMismatch, rangeStart: range.Start, rangeEnd: range.End);
                await ProjectAudioMix.RenderAsync(media, clips, Path.Combine(destination, name), range.End - range.Start, source.SampleRate, ProjectAudioMix.Channels(project), stemProgress, cancellationToken);
            }
        }
        project = project with
        {
            Segments = project.Segments.Select(s => partial || s.ExcludedFromDubbing || project.Settings.DubMuted ||
                Math.Abs(ProjectAudioMix.EffectiveDuration(s).TotalSeconds / s.TargetDuration.TotalSeconds - 1) > project.Settings.DurationTolerance ? s : s with { Status = SegmentStatus.Ready,
                DirtyStages = s.DirtyStages.Except([PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux]).ToArray() }).ToArray()
        };
        await File.WriteAllTextAsync(Path.Combine(destination, "render-metadata.json"), System.Text.Json.JsonSerializer.Serialize(new
        { schemaVersion = 1, applicationVersion = typeof(ExportVideoService).Assembly.GetName().Version?.ToString(), exportOptions = options, project.Engines, project.Settings, project.Metrics,
            execution = metadata?.Describe(), availableEngineVersions = registry is null ? null : Enum.GetValues<EngineTask>().SelectMany(registry.GetAvailableEngines).Select(e => new { e.EngineId, e.Version }),
            segments = project.Segments.Select(s => new { s.Id, s.ExcludedFromDubbing, s.Tts, s.GeneratedSpeech, s.FittedSpeech }) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (options is not null)
        {
            progress?.Report(new(.90, "Export · publishing output files…"));
            Directory.CreateDirectory(Path.GetDirectoryName(finalDestination)!);
            if (Directory.Exists(finalDestination)) Directory.Delete(finalDestination, false);
            // Copy to a sibling staging folder so publication works across volumes.
            string staging = finalDestination + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                var files = Directory.GetFiles(destination);
                long totalBytes = files.Sum(file => new FileInfo(file).Length), copied = 0;
                int lastPercent = -1;
                var buffer = new byte[131072];
                foreach (string file in files)
                {
                    await using var sourceFile = File.OpenRead(file);
                    await using var targetFile = new FileStream(Path.Combine(staging, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    int read;
                    while ((read = await sourceFile.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await targetFile.WriteAsync(buffer.AsMemory(0, read), cancellationToken); copied += read;
                        int percent = (int)(100d * copied / Math.Max(1, totalBytes));
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(new(.90 + .09 * copied / Math.Max(1d, totalBytes), "Export · publishing " + Path.GetFileName(file)));
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested(); Directory.Move(staging, finalDestination);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        string exportedFile = Path.Combine(finalDestination, options?.Profile == "Audio only"
            ? "dub_track." + options.AudioFormat.ToLowerInvariant()
            : options?.Profile == "Editing package" ? "dub_track.wav" : "dubbed." + container);
        string relativeExport = Path.GetRelativePath(directory, exportedFile);
        project = project with { LastExportFile = relativeExport.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relativeExport) ? exportedFile : relativeExport };
        await store.SaveAsync(project, directory, cancellationToken);
        progress?.Report(new(1, "Export complete."));
        return project;
        }
        finally { if (options is not null && Directory.Exists(destination)) Directory.Delete(destination, true); }
    }
}
