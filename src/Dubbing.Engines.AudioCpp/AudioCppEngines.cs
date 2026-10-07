using System.Globalization;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;
using Dubbing.Infrastructure.Models;

namespace Dubbing.Engines.AudioCpp;

public static class AudioCppResults
{
    public static int SampleRate(JsonElement root, int fallback = 16000) => root.TryGetProperty("sample_rate", out var value) ? value.GetInt32() : fallback;
    public static IReadOnlyList<WordTiming> Words(JsonElement root)
    {
        int rate = SampleRate(root);
        return Array(root, "words").Select(w => new WordTiming(Text(w, "word"), Time(w, "start_sample", rate), Time(w, "end_sample", rate), Confidence(w))).ToArray();
    }
    public static IEnumerable<JsonElement> Array(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    public static string Text(JsonElement element, string name, string fallback = "") => element.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
    public static TimeSpan Time(JsonElement item, string field, int rate)
    {
        if (rate <= 0) throw new EngineException("Invalid audio timestamp sample rate.");
        var offset = item.GetProperty(field).GetInt64();
        if (offset < 0) throw new EngineException("Invalid negative audio timestamp.");
        return TimeSpan.FromSeconds((double)offset / rate);
    }
    public static float? Confidence(JsonElement item) => item.TryGetProperty("confidence", out var c) ? c.GetSingle() : null;
}

public sealed class AudioCppTranscriptionEngine(CatalogModel model, IAudioCppTransport transport, IMediaEngine media) : ITranscriptionEngine
{
    public EngineDescriptor Descriptor => model.Descriptor;
    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        var duration = request.Duration ?? (await media.ProbeAsync(request.Audio.Path, cancellationToken)).Duration;
        var scratch = Path.Combine(Path.GetTempPath(), "dubbing-asr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var segments = new List<TranscriptSegment>();
            var words = new List<WordTiming>();
            var language = request.Language ?? "und";
            for (var start = TimeSpan.Zero; start < duration; start += TimeSpan.FromSeconds(15))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = TimeSpan.FromSeconds(Math.Min(15, (duration - start).TotalSeconds));
                var path = Path.Combine(scratch, "chunk.wav");
                await media.CutAsync(request.Audio.Path, path, start, length, cancellationToken);
                progress?.Report(new(start.TotalSeconds / duration.TotalSeconds, $"Transcribing audio ({start.TotalSeconds:0}/{duration.TotalSeconds:0} s)…"));
                var part = await TranscribeChunkAsync(new(new(path), request.Language, length), null, cancellationToken);
                language = part.Language;
                // Keep the exact audio boundary paired with its transcript for forced alignment.
                if (!string.IsNullOrWhiteSpace(part.Text)) segments.Add(new(part.Text, start, start + length));
                words.AddRange((part.Words ?? []).Select(w => w with { Start = w.Start + start, End = w.End + start }));
            }
            progress?.Report(new(1, "Transcription complete."));
            return new(string.Join(" ", segments.Select(s => s.Text)), language, segments, words);
        }
        finally { Directory.Delete(scratch, true); }
    }
    private async Task<TranscriptionResult> TranscribeChunkAsync(TranscriptionRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(0, "Transcribing audio…"));
        var result = await transport.JsonAsync(model, "v1/audio/transcriptions/details", new { model = model.Id,
            audio = request.Audio.Path, language = model.LanguageNames.GetValueOrDefault(request.Language ?? "", request.Language ?? "") }, cancellationToken);
        int rate = AudioCppResults.SampleRate(result);
        var words = AudioCppResults.Words(result);
        string text = AudioCppResults.Text(result, "text");
        var segments = AudioCppResults.Array(result, "segments").Where(s => !string.IsNullOrWhiteSpace(AudioCppResults.Text(s, "text")))
            .Select(s => new TranscriptSegment(AudioCppResults.Text(s, "text"), AudioCppResults.Time(s, "start_sample", rate), AudioCppResults.Time(s, "end_sample", rate), Confidence: AudioCppResults.Confidence(s))).ToArray();
        if (segments.Length == 0 && !string.IsNullOrWhiteSpace(text))
            segments = [new(text, TimeSpan.Zero, request.Duration ?? words.LastOrDefault()?.End ?? throw new EngineException("Transcription returned no duration or timestamps."), words)];
        progress?.Report(new(1, "Transcription complete."));
        string language = AudioCppResults.Text(result, "language", request.Language ?? "und");
        language = model.LanguageNames.FirstOrDefault(p => p.Value.Equals(language, StringComparison.OrdinalIgnoreCase)).Key ?? language;
        return new(text, language, segments, words);
    }
}

public sealed class AudioCppDiarizationEngine(CatalogModel model, IAudioCppTransport transport) : IDiarizationEngine
{
    public EngineDescriptor Descriptor => model.Descriptor;
    public async Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(0, "Identifying speakers…"));
        var result = await transport.JsonAsync(model, "v1/audio/transcriptions/details", new { model = model.Id, audio = request.Audio.Path }, cancellationToken);
        int rate = AudioCppResults.SampleRate(result);
        var turns = AudioCppResults.Array(result, "speaker_turns").Select(t => new SpeakerTurn(AudioCppResults.Time(t, "start_sample", rate),
            AudioCppResults.Time(t, "end_sample", rate), AudioCppResults.Text(t, "speaker_id"), AudioCppResults.Confidence(t))).ToArray();
        progress?.Report(new(1, "Speaker analysis complete."));
        return new(turns);
    }
}

public sealed class AudioCppAlignmentEngine(CatalogModel model, IAudioCppTransport transport) : IAlignmentEngine
{
    public EngineDescriptor Descriptor => model.Descriptor;
    public async Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken cancellationToken)
    {
        var result = await transport.JsonAsync(model, "v1/tasks/run", new { model = model.Id, request = new
        { audio = request.Audio.Path, text = request.Text, language = model.LanguageNames.GetValueOrDefault(request.Language, request.Language) } }, cancellationToken);
        return new(AudioCppResults.Words(result));
    }
}

public sealed class AudioCppSeparationEngine(CatalogModel model, IAudioCppTransport transport) : ISeparationEngine
{
    public EngineDescriptor Descriptor => model.Descriptor;
    public async Task<SeparationResult> SeparateAsync(SeparationRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(0, "Separating dialogue and background…"));
        await transport.SeparateAsync(model, request.Audio.Path, request.OutputDirectory, cancellationToken);
        var stems = Directory.GetFiles(request.OutputDirectory, "*.wav").ToDictionary(p => Path.GetFileNameWithoutExtension(p)!, p => new AudioReference(p));
        if (stems.Count == 0) throw new EngineException("Source separation produced no stems.");
        progress?.Report(new(1, "Source separation complete."));
        return new(stems);
    }
}

public sealed class AudioCppTtsEngine(CatalogModel model, IAudioCppTransport transport, IMediaEngine media) : ITtsEngine
{
    public EngineDescriptor Descriptor => model.Family == "index_tts2"
        ? model.Descriptor with { Version = model.Descriptor.Version + "+explicit-language-v1-native-speed-v1" }
        : model.Descriptor;
    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        var language = model.LanguageNames.GetValueOrDefault(request.Language, request.Language);
        if (model.Family == "index_tts2")
        {
            language = request.Language.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
            if (language is not ("zh" or "en" or "ja" or "es" or "ar"))
                throw new EngineException("IndexTTS2.5 requires a dubbing target language: zh, en, ja, es or ar.");
            if (request.SpeakingRate is { } rate && (!double.IsFinite(rate) || rate < 0.5 || rate > 2))
                throw new EngineException("IndexTTS2.5 speed must be between 0.5 and 2.");
            if (request.VoiceReference is null) throw new EngineException("Choose or import a voice reference for this speaker before generating speech.");
            if (request.Emotion?.Mode == EmotionMode.Vector && request.Emotion.Vector?.Count != 8)
                throw new EngineException("This model requires an emotion vector with eight values.");
        }
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!, "reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var body = new Dictionary<string, object?> { ["model"] = model.Id, ["input"] = request.Text,
                ["language"] = language, ["response_format"] = "wav" };
            var options = new Dictionary<string, object?>(request.EngineOptions);
            if (model.Family == "index_tts2")
            {
                // audio.cpp v0.9.0 IndexTTS reads options, not text_input.language.
                // The dubbing target takes precedence over advanced engine options.
                foreach (var key in options.Keys.Where(k => k.Equals("lang", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("language", StringComparison.OrdinalIgnoreCase)).ToArray()) options.Remove(key);
                options["language"] = language;
                if (request.SpeakingRate is { } speed)
                {
                    // Native synthesis duration scaling, not waveform resampling.
                    options["duration_factor"] = 1 / speed;
                    progress?.Report(new(0, FormattableString.Invariant($"IndexTTS2.5 · speed={speed:F3} · duration_factor={1 / speed:F3}")));
                }
                progress?.Report(new(0, $"IndexTTS2.5 · output language={language} (dubbing target)"));
            }
            if (request.Seed.HasValue) body["seed"] = request.Seed.Value.ToString(CultureInfo.InvariantCulture);
            if (request.SpeakingRate.HasValue && model.Family != "index_tts2") body["speed"] = request.SpeakingRate;
            var referenceWatch = System.Diagnostics.Stopwatch.StartNew();
            var preparedReferences = new Dictionary<AudioReference, string>();
            async Task<string> PrepareReferenceAsync(AudioReference reference, string name)
            {
                if (preparedReferences.TryGetValue(reference, out var prepared)) return prepared;
                prepared = await ReferenceAsync(reference, name, scratch, request.ReferenceCacheDirectory, progress, cancellationToken);
                preparedReferences[reference] = prepared; return prepared;
            }
            if (request.VoiceReference is { } voice) body["voice_ref"] = await PrepareReferenceAsync(voice, "voice");
            if (request.Emotion is { } emotion)
            {
                if (emotion.ReferenceAudio is { } audio) body[Mapping("emotionAudio")] = await PrepareReferenceAsync(audio, "emotion");
                if (emotion.Intensity.HasValue) options[Mapping("emotionIntensity")] = emotion.Intensity;
                if (emotion.Mode == EmotionMode.Label) body["emotion"] = emotion.Label;
                if (emotion.Mode == EmotionMode.Instruction) { options[Mapping("emotionTextEnabled")] = true; options[Mapping("emotionText")] = emotion.Instruction; }
                if (emotion.Mode == EmotionMode.Vector) options[Mapping("emotionVector")] = string.Join(',', emotion.Vector!.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            }
            body["options"] = options;
            progress?.Report(new(.05, $"Voice and emotion reference preparation · {referenceWatch.Elapsed.TotalSeconds:F2} s"));
            progress?.Report(new(.05, "Generating speech…"));
            await transport.SpeechAsync(model, body, request.OutputPath, cancellationToken);
            var info = await media.ProbeAsync(request.OutputPath, cancellationToken);
            var stream = info.Streams.First(s => s.Type == "audio");
            progress?.Report(new(1, "Speech generated."));
            return new(new(request.OutputPath), info.Duration, stream.SampleRate ?? 24000, stream.Channels ?? 1);
        }
        finally { Directory.Delete(scratch, true); }
    }
    private string Mapping(string key) => model.FieldMappings.GetValueOrDefault(key) ?? throw new UnsupportedCapabilityException($"Model manifest has no mapping for {key}.");
    private async Task<string> ReferenceAsync(AudioReference reference, string name, string directory, string? cacheDirectory, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference.Path) || !File.Exists(reference.Path)) throw new EngineException($"Choose an existing {name} reference audio file.");
        string? cachedPath = null;
        if (cacheDirectory is not null && (reference.Start is not null || reference.End is not null))
        {
            var file = new FileInfo(reference.Path);
            string key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { file.FullName, file.Length, file.LastWriteTimeUtc, reference.Fingerprint, reference.Start, reference.End, model.Family })));
            Directory.CreateDirectory(cacheDirectory);
            cachedPath = Path.Combine(cacheDirectory, key + ".wav");
            if (File.Exists(cachedPath))
            {
                progress?.Report(new(0, $"Reusing prepared {name} reference."));
                return cachedPath;
            }
        }
        var info = await media.ProbeAsync(reference.Path, ct);
        var start = reference.Start ?? TimeSpan.Zero;
        var end = reference.End ?? info.Duration;
        if (start < TimeSpan.Zero || end > info.Duration + TimeSpan.FromMilliseconds(1) || end <= start)
            throw new EngineException($"Invalid {name} reference audio range.");
        if (model.Family == "index_tts2" && end - start < TimeSpan.FromSeconds(1))
            throw new EngineException($"Use at least 1 second of {name} reference audio. Shorter IndexTTS references are blocked to prevent native crashes. Choose a longer speech reference or exclude this non-speech segment.");
        if (reference.Start is null && reference.End is null) return Path.GetFullPath(reference.Path);
        string output = Path.Combine(directory, name + ".wav");
        await media.CutAsync(reference.Path, output, start, end - start, ct);
        if (cachedPath is not null)
        {
            ct.ThrowIfCancellationRequested();
            File.Move(output, cachedPath, overwrite: true); return cachedPath;
        }
        return output;
    }
}
