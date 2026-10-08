using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class ImportVideoService(IMediaEngine media, IProjectStore store, IEngineRegistry registry, TimelineBuilder timeline)
{
    public async Task<DubbingProject> ImportAsync(DubbingProject project, string input, string directory,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(0, "Analyzing video"));
        var info = await media.ProbeAsync(input, cancellationToken);
        if (!info.Streams.Any(x => x.Type == "video")) throw new InvalidOperationException("Select a file containing video.");
        var audioStream = info.Streams.FirstOrDefault(x => x.Type == "audio")
            ?? throw new InvalidOperationException("The video contains no audio track.");
        var fingerprint = await ContentFingerprint.ComputeAsync(input, cancellationToken);
        bool sourceChanged = project.Source is not null && project.Source.Fingerprint != fingerprint;
        if (sourceChanged) project = project with { Segments = [], Speakers = [], Metrics = [] };
        project = project with { Source = new(Path.GetFullPath(input), fingerprint, info.Duration,
            audioStream.Channels ?? 1, audioStream.SampleRate ?? 48000) };
        await store.SaveAsync(project, directory, cancellationToken);
        project = await new AudioPreparationService(media, registry).PrepareAsync(project, directory, progress, cancellationToken);
        await store.SaveAsync(project, directory, cancellationToken);
        string workPath = Path.Combine(directory, project.AnalysisAudio!.Path);
        fingerprint = StageCache.Key(fingerprint, project.Settings.AnalysisStreamIndex, project.AnalysisAudio.Path);
        var workAudio = new AudioReference(workPath, fingerprint);
        var transcriber = registry.GetEngine<ITranscriptionEngine>(project.Engines.Transcription ?? throw new InvalidOperationException("Select a transcription engine."));
        var transcript = await StageCache.GetAsync(directory, "transcript", StageCache.Key("chunked-v3-60s", fingerprint, transcriber.Descriptor.EngineId, transcriber.Descriptor.Version, project.Settings.SourceLanguage),
            () => transcriber.TranscribeAsync(new(workAudio, project.Settings.SourceLanguage, info.Duration), progress, cancellationToken), cancellationToken);
        var diarizer = registry.GetEngine<IDiarizationEngine>(project.Engines.Diarization ?? throw new InvalidOperationException("Select a diarization engine."));
        var diarization = await StageCache.GetAsync(directory, "diarization", StageCache.Key(fingerprint, diarizer.Descriptor.EngineId, diarizer.Descriptor.Version),
            () => diarizer.DiarizeAsync(new(workAudio, info.Duration), progress, cancellationToken), cancellationToken);
        if (project.Settings.UseAlignment && project.Engines.Alignment is { } alignmentId && !string.IsNullOrWhiteSpace(transcript.Text))
        {
            var aligner = registry.GetEngine<IAlignmentEngine>(alignmentId);
            progress?.Report(new(0, "Aligning words…"));
            var alignment = await StageCache.GetAsync(directory, "alignment", StageCache.Key("chunked-v4-clamped", fingerprint, transcript.Segments, aligner.Descriptor.EngineId, aligner.Descriptor.Version, project.Settings.SourceLanguage),
                () => new ChunkAlignmentService(media).AlignAsync(aligner, workAudio, transcript, project.Settings.SourceLanguage, progress, cancellationToken), cancellationToken);
            transcript = transcript with { Words = alignment.Words };
        }
        var oldSegments = project.Segments;
        var oldSpeakers = project.Speakers;
        var built = timeline.Build(transcript, diarization, project.Settings.TargetLanguage);
        built = TranscriptQuality.ExcludeSuspectRepetitions(built);
        int suspectCount = built.Count(s => s.ExclusionReason is not null);
        if (suspectCount > 0) progress?.Report(new(0, $"Excluded {suspectCount} suspected repetitive ASR segments without a detected speaker. Review the original audio; text and timing were retained."));
        if (project.Settings.NormalizeSegments)
        {
            progress?.Report(new(0, "Normalizing dialogue into semantic segments…"));
            built = new SegmentNormalizer().Normalize(built, project.Settings.ParagraphSegments);
            progress?.Report(new(1, $"Dialogue normalized into {built.Count} segments."));
        }
        var expanded = ShortSegmentTiming.Expand(built, info.Duration);
        int adjusted = expanded.Zip(built).Count(pair => pair.First.Start != pair.Second.Start || pair.First.End != pair.Second.End);
        if (adjusted > 0) progress?.Report(new(1, $"Extended {adjusted} short dialogue segments using available gaps."));
        built = expanded;
        project = project with
        {
            Segments = built.Select(s =>
            {
                var old = oldSegments.FirstOrDefault(o => o.Start == s.Start && o.End == s.End && o.Source.Text == s.Source.Text);
                return old is null ? s with { Id = Guid.NewGuid().ToString("N") } : s with { Id = old.Id, Translation = old.Translation, Emotion = old.Emotion, Tts = old.Tts,
                    GeneratedSpeech = old.GeneratedSpeech, FittedSpeech = old.FittedSpeech, SpeakerId = old.SpeakerId, ExcludedFromDubbing = old.ExcludedFromDubbing, ExclusionReason = old.ExclusionReason };
            }).ToArray(),
            Speakers = diarization.Turns.Select(x => x.SpeakerId).Distinct().Select((id, index) =>
                oldSpeakers.FirstOrDefault(s => s.Id == id) ?? new SpeakerProfile { Id = id, DisplayName = $"Speaker {index + 1}" }).ToArray()
        };
        var referencedSpeakerIds = project.Segments.Select(s => s.SpeakerId).ToHashSet();
        project = project with { Speakers = project.Speakers.Concat(oldSpeakers.Where(s => referencedSpeakerIds.Contains(s.Id) && project.Speakers.All(p => p.Id != s.Id))).ToArray() };
        if (project.Settings.AutomaticVoiceReferences)
            project = await new VoiceReferenceSelector(media).SelectAsync(project, directory, cancellationToken);
        await store.SaveAsync(project, directory, cancellationToken);
        progress?.Report(new(1, "Video imported; segments ready"));
        return project;
    }
}
