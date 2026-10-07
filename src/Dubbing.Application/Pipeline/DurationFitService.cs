using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed record DurationFitResult(DubSegment Segment, GeneratedSpeech Speech, GeneratedSpeech? Fitted, int Retries);

public sealed record DurationFitWork(DubSegment Segment, ITtsEngine Engine, TtsRequest Request, GeneratedSpeech Speech);

public sealed class DurationFitService(IMediaEngine media, IEngineRegistry registry)
{
    public async Task<DurationFitResult> FitAsync(DubbingProject project, DubSegment segment, ITtsEngine engine,
        TtsRequest request, GeneratedSpeech speech, string directory, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var result = await FitBatchAsync(project, [new(segment, engine, request, speech)], directory, progress, ct);
        return result[0];
    }

    public async Task<IReadOnlyList<DurationFitResult>> FitBatchAsync(DubbingProject project, IReadOnlyList<DurationFitWork> input,
        string directory, IProgress<EngineProgress>? progress, CancellationToken ct,
        Func<DurationFitResult, Task>? checkpoint = null, string? workingDirectory = null)
    {
        var work = input.ToArray();
        var retries = new int[work.Length]; var rewrites = new int[work.Length];
        for (int round = 0; round < project.Settings.MaximumTtsRetries; round++)
        {
            var pending = new List<int>();
            // Finish all translation adjustments before the next TTS pass: one resident engine per phase.
            for (int i = 0; i < work.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (segment, engine, request, speech) = work[i];
                if (Math.Abs(speech.Duration.TotalSeconds / segment.TargetDuration.TotalSeconds - 1) <= project.Settings.DurationTolerance) continue;
                progress?.Report(new((round + .5 * i / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1), $"Duration adjustment pass {round + 1}: preparing segment {i + 1}/{work.Length}", segment.Id));
                double ratio = speech.Duration.TotalSeconds / segment.TargetDuration.TotalSeconds;
                if (engine.Descriptor.Capabilities.SupportsSpeakingRate)
                {
                    double rate = Math.Clamp((request.SpeakingRate ?? 1) * ratio, 0.75, 1.3);
                    if (Math.Abs(rate - (request.SpeakingRate ?? 1)) < 0.001) continue;
                    progress?.Report(new((round + .5 * i / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1), $"Duration adjustment: speech {speech.Duration.TotalSeconds:F2} s, target {segment.TargetDuration.TotalSeconds:F2} s; native speed {rate:F3}, preserving translation", segment.Id));
                    request = request with { SpeakingRate = rate };
                    segment = segment with { Tts = segment.Tts with { SpeakingRate = rate } };
                }
                else if (!segment.Translation.Locked && project.Engines.Translation is { } translator && rewrites[i] < project.Settings.MaximumTranslationRetries)
                {
                    progress?.Report(new((round + .5 * i / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1), $"Duration adjustment: speech {speech.Duration.TotalSeconds:F2} s, target {segment.TargetDuration.TotalSeconds:F2} s; requesting a translation rewrite", segment.Id));
                    var translated = await registry.GetEngine<ITranslationEngine>(translator).TranslateAsync(new(segment.Id, segment.Source.Text,
                        segment.Source.Language, segment.Translation.Language)
                    {
                        TargetDuration = segment.TargetDuration, SpeakerId = segment.SpeakerId, Glossary = project.Settings.Glossary,
                        ModelId = project.Settings.TranslationModelId,
                        StyleInstructions = $"Previous wording: {segment.Translation.Text}. Actual speech took {speech.Duration.TotalSeconds:F2} seconds. " +
                            $"Rewrite {(ratio > 1 ? "more concisely" : "slightly longer")} for {segment.TargetDuration.TotalSeconds:F2} seconds. Preserve all meaning."
                    }, ct);
                    if (translated.SegmentId != segment.Id || translated.Language != segment.Translation.Language || string.IsNullOrWhiteSpace(translated.Text))
                        throw new InvalidDataException("Duration adaptation returned an invalid translation.");
                    rewrites[i]++;
                    if (translated.Text == segment.Translation.Text)
                    {
                        progress?.Report(new((round + .5 * i / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1), "Duration adjustment: translation unchanged; keeping existing speech", segment.Id));
                        continue;
                    }
                    segment = segment with { Translation = segment.Translation with { Text = translated.Text } };
                    request = request with { Text = translated.Text };
                }
                else continue;
                work[i] = new(segment, engine, request, speech); pending.Add(i);
            }
            if (pending.Count == 0) break;
            foreach (var group in pending.GroupBy(i => work[i].Engine.Descriptor.EngineId))
            foreach (int i in group)
            {
                ct.ThrowIfCancellationRequested();
                var (segment, engine, request, speech) = work[i];
                progress?.Report(new((round + .5 + .5 * i / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1), $"Duration adjustment pass {round + 1}: synthesizing segment {i + 1}/{work.Length}", segment.Id));
                string key = TtsCacheKey.Create(engine.Descriptor, segment.Tts.ModelId, segment.Tts.ModelVersion, request);
                string relative = $"{workingDirectory ?? "generated/tts"}/{key}-{Guid.NewGuid():N}.wav", output = Path.Combine(directory, relative);
                var result = await engine.SynthesizeAsync(request with { OutputPath = output }, null, ct);
                speech = new(relative, result.Duration, key, await ContentFingerprint.ComputeAsync(output, ct)); retries[i]++;
                work[i] = new(segment, engine, request, speech);
                // Retry WAVs and rewritten text stay provisional until the final fitting pass.
            }
        }
        var results = new List<DurationFitResult>();
        for (int i = 0; i < work.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (segment, engine, request, speech) = work[i];
            progress?.Report(new((project.Settings.MaximumTtsRetries + (double)i / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1), "Final duration pass: measuring and applying permitted time stretch", segment.Id));
            GeneratedSpeech? fitted = null;
            double speed = speech.Duration.TotalSeconds / segment.TargetDuration.TotalSeconds;
            if (Math.Abs(speed - 1) > 0.001 && Math.Abs(speed - 1) <= project.Settings.MaximumTimeStretch)
            {
                string key = StageCache.Key(speech.CacheKey, speech.Fingerprint, segment.TargetDuration, speed);
                string file = $"{workingDirectory ?? "generated/tts"}/{key}-{Guid.NewGuid():N}-fit.wav";
                await media.StretchAsync(Path.Combine(directory, speech.File), Path.Combine(directory, file), speed, ct);
                var info = await media.ProbeAsync(Path.Combine(directory, file), ct);
                fitted = new(file, info.Duration, key, await ContentFingerprint.ComputeAsync(Path.Combine(directory, file), ct));
            }
            var result = new DurationFitResult(segment, speech, fitted, retries[i]);
            results.Add(result);
            if (checkpoint is not null) await checkpoint(result);
            progress?.Report(new((project.Settings.MaximumTtsRetries + (i + 1d) / Math.Max(1, work.Length)) / (project.Settings.MaximumTtsRetries + 1),
                $"Final duration: {(fitted ?? speech).Duration.TotalSeconds:F2} s / {segment.TargetDuration.TotalSeconds:F2} s target", segment.Id));
        }
        return results;
    }
}
