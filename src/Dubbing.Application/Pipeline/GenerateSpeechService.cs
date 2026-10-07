using Dubbing.Application.Engines;
using Dubbing.Application.Media;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class GenerateSpeechService(EngineSelectionService selection, IProjectStore store, DurationFitService? timing = null, Dubbing.Core.Media.IMediaEngine? media = null)
{
    public async Task<DubbingProject> GenerateAsync(DubbingProject project, string directory,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken, string? segmentId = null, bool fitDuration = true, IReadOnlySet<string>? segmentIds = null, bool? overwriteExisting = null)
    {
        var published = project;
        var publishedSegments = project.Segments.ToArray();
        var metrics = new Dictionary<string, JobMetric>();
        using var candidates = new SpeechWorkspace(directory);
        var operationProgress = progress;
        progress = new PhaseProgress(operationProgress, 0, fitDuration && timing is not null ? .45 : .95, "Speech generation");
        if (project.AudioTracks.Count > 0 && project.SelectedSourceAudio is null)
            throw new InvalidOperationException("Audio track settings changed. Prepare audio before generating speech.");
        if (project.Source is { } source)
        {
            var expanded = ShortSegmentTiming.Expand(project.Segments, source.Duration, segmentId);
            foreach (var pair in expanded.Zip(project.Segments))
            {
                if (pair.First.Start != pair.Second.Start || pair.First.End != pair.Second.End)
                    progress?.Report(new(0, $"Short segment extended: {pair.Second.TargetDuration.TotalSeconds:F2} s to {pair.First.TargetDuration.TotalSeconds:F2} s", pair.First.Id));
                else if ((segmentId is null || pair.First.Id == segmentId) && !pair.First.ExcludedFromDubbing && pair.First.TargetDuration.TotalSeconds < 1 && !string.IsNullOrWhiteSpace(pair.First.Translation.Text))
                    progress?.Report(new(0, "Short segment retained: insufficient free space to extend without overlapping. Source reference context will be expanded separately.", pair.First.Id));
            }
            expanded = expanded.Select((s, i) => (segmentIds is null || segmentIds.Contains(s.Id)) &&
                (overwriteExisting != false || project.Segments[i].GeneratedSpeech is null) ? s : project.Segments[i]).ToArray();
            project = project with { Segments = expanded };
        }
        var segments = project.Segments.ToArray();
        var fitting = new List<DurationFitWork>();
        var eligible = Enumerable.Range(0, segments.Length).Where(i => !segments[i].ExcludedFromDubbing && (segmentId is null || segments[i].Id == segmentId))
            .Where(i => segmentId is not null || (!string.IsNullOrWhiteSpace(segments[i].Translation.Text) && segments[i].Translation.Language == project.Settings.TargetLanguage))
            .Where(i => (segmentIds is null || segmentIds.Contains(segments[i].Id)) &&
                (overwriteExisting != false || segments[i].GeneratedSpeech is null || !File.Exists(Path.Combine(directory, segments[i].GeneratedSpeech!.File))))
            .GroupBy(i => selection.ResolveTts(project, segments[i]).Descriptor.EngineId).SelectMany(g => g).ToArray();
        if (media is not null)
        {
            var checkedReferences = new HashSet<string>();
            var problems = new List<string>();
            foreach (int i in eligible)
            {
                var segment = segments[i];
                var engine = selection.ResolveTts(project, segment);
                if (!engine.Descriptor.EngineId.Contains("index", StringComparison.OrdinalIgnoreCase)) continue;
                var speaker = project.Speakers.FirstOrDefault(s => s.Id == segment.SpeakerId);
                if (speaker?.VoiceReference is not { } voice || (project.Settings.UseSourceVoicePerSegment && !speaker.HasManualVoiceReference && project.Source is not null)) continue;
                if (!checkedReferences.Add(speaker.Id)) continue;
                string path = Path.GetFullPath(Path.Combine(directory, voice.Path));
                if (!File.Exists(path)) { problems.Add($"{speaker.DisplayName}: missing voice reference"); continue; }
                var info = await media.ProbeAsync(path, cancellationToken);
                double duration = ((voice.End ?? info.Duration) - (voice.Start ?? TimeSpan.Zero)).TotalSeconds;
                if (duration < 1 || (voice.Start ?? TimeSpan.Zero) < TimeSpan.Zero || (voice.End ?? info.Duration) > info.Duration)
                    problems.Add($"{speaker.DisplayName}: {duration:F2} s ({Path.GetFileName(path)})");
            }
            if (problems.Count > 0) throw new EngineException("Invalid IndexTTS voice references. Select or adjust a reference of at least 1 second in Speakers before generating speech: " + string.Join("; ", problems));
        }
        for (int position = 0; position < eligible.Length; position++)
        {
            int index = eligible[position];
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            if (segment.ExcludedFromDubbing) continue;
            progress?.Report(new((double)position / eligible.Length, $"TTS pass · segment {position + 1}/{eligible.Length} ({segment.Start.TotalSeconds:F2} s)", segment.Id));
            if (segmentId is not null && segment.Id != segmentId) continue;
            if (segment.DirtyStages.Any(x => x is PipelineStage.Transcription or PipelineStage.Diarization or PipelineStage.Alignment or PipelineStage.Timeline))
                throw new InvalidOperationException("Analysis settings have changed. Select Reanalyze to rebuild segments.");
            if (string.IsNullOrWhiteSpace(segment.Translation.Text) || segment.Translation.Language != project.Settings.TargetLanguage)
                throw new InvalidOperationException($"Translate segment {segment.Id} into the target language before generating speech.");
            var engine = selection.ResolveTts(project, segment);
            var capabilities = engine.Descriptor.Capabilities;
            if (segment.Tts.SpeakingRate.HasValue && !capabilities.SupportsSpeakingRate)
                throw new UnsupportedCapabilityException("The speech engine does not support a configurable speed.");
            bool emotionSupported = segment.Emotion.Mode switch
            {
                EmotionMode.None => true,
                EmotionMode.SourceAudio or EmotionMode.ReferenceAudio => capabilities.SupportsEmotionReferenceAudio,
                EmotionMode.Label => capabilities.SupportsEmotionLabels,
                EmotionMode.Vector => capabilities.SupportsEmotionVector,
                EmotionMode.Instruction => capabilities.SupportsEmotionInstruction,
                _ => false
            };
            if (!emotionSupported) throw new UnsupportedCapabilityException("The speech engine does not support this emotion control.");
            var emotion = segment.Emotion;
            string sourceAudio = project.Stems.TryGetValue("vocals", out var vocals)
                ? (Path.IsPathRooted(vocals.Path) ? vocals.Path : Path.Combine(directory, vocals.Path))
                : Path.Combine(directory, project.SelectedSourceAudio?.Path ?? "assets/extracted/master.wav");
            if (!File.Exists(sourceAudio)) sourceAudio = Path.Combine(directory, project.AnalysisAudio?.Path ?? "assets/extracted/analysis.wav");
            var sourceReference = SourceReference(project, segment, sourceAudio);
            if (emotion.Mode == EmotionMode.None && project.Settings.AutomaticEmotion && project.Source is not null && capabilities.SupportsEmotionReferenceAudio)
                emotion = new() { Mode = EmotionMode.SourceAudio, Intensity = 1.0 };
            if (emotion.Mode == EmotionMode.SourceAudio)
                emotion = emotion with { ReferenceAudio = sourceReference };
            if (emotion.Mode == EmotionMode.ReferenceAudio && string.IsNullOrWhiteSpace(emotion.ReferenceAudio?.Path))
                throw new EngineException($"Segment {index + 1}: choose an emotion reference file or select Source audio.");
            if (emotion.ReferenceAudio is { } emotionAudio)
            {
                string path = Path.IsPathRooted(emotionAudio.Path) ? emotionAudio.Path : Path.Combine(directory, emotionAudio.Path);
                emotion = emotion with { ReferenceAudio = emotionAudio with { Path = path,
                    Fingerprint = await ContentFingerprint.ComputeAsync(path, cancellationToken) } };
            }
            var speaker = project.Speakers.FirstOrDefault(x => x.Id == segment.SpeakerId);
            AudioReference? reference = null;
            if (project.Settings.UseSourceVoicePerSegment && speaker?.HasManualVoiceReference != true && project.Source is not null && capabilities.SupportsVoiceCloning)
                reference = sourceReference with { Fingerprint = await ContentFingerprint.ComputeAsync(sourceAudio, cancellationToken) };
            else if (speaker?.VoiceReference is { } voice && capabilities.SupportsVoiceCloning)
            {
                var path = Path.IsPathRooted(voice.Path) ? voice.Path : Path.Combine(directory, voice.Path);
                reference = voice with { Path = path, Fingerprint = await ContentFingerprint.ComputeAsync(path, cancellationToken) };
            }
            progress?.Report(new((double)position / eligible.Length,
                $"Voice reference: {(speaker?.HasManualVoiceReference == true ? speaker.DisplayName + " (manual)" : project.Settings.UseSourceVoicePerSegment ? "original segment" : speaker?.DisplayName ?? "none")} · {Path.GetFileName(reference?.Path ?? "none")} · emotion: {emotion.Mode}", segment.Id));
            var request = new TtsRequest
            {
                Text = segment.Translation.Text, Language = segment.Translation.Language, OutputPath = "",
                SpeakerProfileId = segment.SpeakerId, VoiceReference = reference,
                Emotion = emotion.Mode == EmotionMode.None ? null : emotion,
                TargetDuration = capabilities.SupportsTargetDuration ? segment.TargetDuration : null,
                SpeakingRate = segment.Tts.SpeakingRate, Seed = capabilities.SupportsSeed ? segment.Tts.Seed : null,
                EngineOptions = segment.Tts.EngineOptions
            };
            string key = TtsCacheKey.Create(engine.Descriptor, segment.Tts.ModelId, segment.Tts.ModelVersion, request);
            string relative = overwriteExisting != true && segment.GeneratedSpeech is { } cached && cached.CacheKey == key ? cached.File : "";
            string output = Path.Combine(directory, relative);
            var previous = segment.GeneratedSpeech;
            var started = System.Diagnostics.Stopwatch.StartNew();
            bool validCache = overwriteExisting != true && previous is not null && previous.CacheKey == key && previous.File == relative &&
                File.Exists(output) && previous.Fingerprint is not null &&
                await ContentFingerprint.ComputeAsync(output, cancellationToken) == previous.Fingerprint;
            if (!validCache)
            {
                relative = $"{candidates.RelativeDirectory}/{key}-{Guid.NewGuid():N}.wav";
                output = Path.Combine(directory, relative);
                var result = await engine.SynthesizeAsync(request with { OutputPath = output },
                    new PhaseProgress(progress, (double)position / eligible.Length, 1d / eligible.Length, "Candidate"), cancellationToken);
                if (!File.Exists(output) || result.Duration <= TimeSpan.Zero) throw new EngineException("The engine did not generate valid audio.");
                previous = new(relative, result.Duration, key, await ContentFingerprint.ComputeAsync(output, cancellationToken));
            }
            GeneratedSpeech? fitted = validCache && fitDuration ? segment.FittedSpeech : null;
            int retries = 0;
            bool fitValid = validCache && fitted is not null && !segment.DirtyStages.Contains(PipelineStage.DurationFit) &&
                File.Exists(Path.Combine(directory, fitted.File)) && fitted.Fingerprint == await ContentFingerprint.ComputeAsync(Path.Combine(directory, fitted.File), cancellationToken);
            bool needsFitting = fitDuration && timing is not null && !fitValid;
            if (needsFitting) fitting.Add(new(segment, engine, request, previous!));
            segments[index] = segment with
            {
                GeneratedSpeech = previous, FittedSpeech = fitted,
                Status = Math.Abs((fitted ?? previous!).Duration.TotalSeconds / segment.TargetDuration.TotalSeconds - 1) > project.Settings.DurationTolerance ? SegmentStatus.Error : SegmentStatus.TtsGenerated,
                DirtyStages = segment.DirtyStages.Where(x => x != PipelineStage.Tts && x != PipelineStage.DurationFit)
                    .Union(fitDuration ? [] : new[] { PipelineStage.DurationFit })
                    .Union(StageDependencies.After(PipelineStage.DurationFit)).Order().ToArray()
            };
            metrics[segment.Id] = new("Tts", started.Elapsed.TotalSeconds, previous!.Duration.TotalSeconds, segment.Id, retries);
            if (!needsFitting) await PublishAsync(index, segments[index]);
            progress?.Report(new((double)(position + 1) / eligible.Length, needsFitting ? "TTS pass · candidate ready; duration fitting pending" : "TTS pass · final speech published", segment.Id));
        }
        if (timing is not null && fitting.Count > 0)
            await timing.FitBatchAsync(project, fitting, directory, new PhaseProgress(operationProgress, .45, .5, "Duration fit"), cancellationToken, async fit =>
            {
                int index = Array.FindIndex(segments, s => s.Id == fit.Segment.Id);
                segments[index] = fit.Segment with { GeneratedSpeech = fit.Speech, FittedSpeech = fit.Fitted,
                    Status = Math.Abs((fit.Fitted ?? fit.Speech).Duration.TotalSeconds / fit.Segment.TargetDuration.TotalSeconds - 1) > project.Settings.DurationTolerance ? SegmentStatus.Error : SegmentStatus.TtsGenerated,
                    DirtyStages = segments[index].DirtyStages };
                metrics[fit.Segment.Id] = metrics[fit.Segment.Id] with { AudioSeconds = (fit.Fitted ?? fit.Speech).Duration.TotalSeconds, Retries = fit.Retries };
                await PublishAsync(index, segments[index]);
            }, candidates.RelativeDirectory);
        operationProgress?.Report(new(1, "Speech operation complete. Final audio is ready for overlap review."));
        return published;

        async Task PublishAsync(int index, DubSegment completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed = completed with { GeneratedSpeech = candidates.Publish(completed.GeneratedSpeech!),
                FittedSpeech = completed.FittedSpeech is { } fit ? candidates.Publish(fit) : null };
            publishedSegments[index] = completed;
            var next = published with { Segments = publishedSegments.ToArray(), Metrics = published.Metrics.Append(metrics[completed.Id]).TakeLast(10000).ToArray() };
            await store.SaveAsync(next, directory, cancellationToken);
            published = next;
        }
    }
    public static AudioReference SourceReference(DubbingProject project, DubSegment segment, string path)
    {
        double duration = project.Source?.Duration.TotalSeconds ?? segment.End.TotalSeconds;
        double referenceStart = (segment.SourceReferenceStart ?? segment.Start).TotalSeconds, referenceEnd = (segment.SourceReferenceEnd ?? segment.End).TotalSeconds;
        double referenceLength = referenceEnd - referenceStart;
        double length = Math.Min(duration, Math.Max(referenceLength < 1 ? 1.2 : 1, referenceLength));
        double start = Math.Clamp((referenceStart + referenceEnd - length) / 2, 0, Math.Max(0, duration - length));
        return new(path, Start: TimeSpan.FromSeconds(start), End: TimeSpan.FromSeconds(start + length));
    }
}
