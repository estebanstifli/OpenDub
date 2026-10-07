using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dubbing.App.Localization;
using Dubbing.Application.Pipeline;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Engines.AudioCpp;
using Dubbing.Engines.LlamaCpp;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Engines;
using Dubbing.Infrastructure.Models;
using Dubbing.Infrastructure.Processes;
using Dubbing.Media.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Integration.Tests;

public sealed class ProviderTests : IDisposable
{
    [FFmpegFact] public async Task BatchRejectsLaterInvalidReferenceBeforeFirstSynthesis()
    {
        Directory.CreateDirectory(root);
        string good = Path.Combine(root, "good.wav"), shortVoice = Path.Combine(root, "short.wav");
        var mock = new MockTtsEngine();
        await mock.SynthesizeAsync(new() { Text = "fixture", Language = "en", OutputPath = good, TargetDuration = TimeSpan.FromSeconds(2) }, null, default);
        await mock.SynthesizeAsync(new() { Text = "fixture", Language = "en", OutputPath = shortVoice, TargetDuration = TimeSpan.FromSeconds(.96) }, null, default);
        int calls = 0;
        var transport = new FakeTransport { BeforeSpeech = _ => { calls++; throw new Exception("Inference must not start"); } };
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media);
        var registry = new EngineRegistry([engine]);
        var store = new Dubbing.Infrastructure.Projects.JsonProjectStore(new Dubbing.Infrastructure.Projects.ProjectJson(), NullLogger<Dubbing.Infrastructure.Projects.JsonProjectStore>.Instance);
        var project = new DubbingProject { Engines = new() { Tts = engine.Descriptor.EngineId },
            Speakers = [new() { Id = "a", DisplayName = "Speaker 1", VoiceReference = new(good), VoiceReferenceIsAutomatic = false },
                new() { Id = "b", DisplayName = "Speaker 2", VoiceReference = new(shortVoice), VoiceReferenceIsAutomatic = false }],
            Segments = [new() { SpeakerId = "a", Translation = new("es", "Hola"), End = TimeSpan.FromSeconds(2) },
                new() { SpeakerId = "b", Translation = new("es", "Una esposa"), Start = TimeSpan.FromSeconds(17.24), End = TimeSpan.FromSeconds(17.8) }] };
        var service = new GenerateSpeechService(new Dubbing.Application.Engines.EngineSelectionService(registry), store, media: Media);
        var error = await Assert.ThrowsAsync<EngineException>(() => service.GenerateAsync(project, root, null, default));
        Assert.Contains("Speaker 2", error.Message); Assert.Contains("short.wav", error.Message); Assert.Equal(0, calls);
    }

    [FFmpegFact] public async Task SpeakerReferenceDefaultsToFirstSegmentAndPreservesExternalReference()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "fixture", Language = "en", OutputPath = source, TargetDuration = TimeSpan.FromSeconds(8) }, null, default);
        var project = new DubbingProject { SelectedSourceAudio = new(source), Speakers = [new() { Id = "a" }, new() { Id = "b", VoiceReference = new("external.wav") }],
            Segments = [new() { Id = "too-short", SpeakerId = "a", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(.3) }, new() { Id = "later", SpeakerId = "a", Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(8) },
                new() { Id = "first", SpeakerId = "a", Start = TimeSpan.FromSeconds(.5), End = TimeSpan.FromSeconds(2) }] };
        var result = await new VoiceReferenceSelector(Media).SelectAsync(project, root, default);
        Assert.Equal("first", result.Speakers[0].VoiceReferenceSegmentId);
        Assert.True(File.Exists(Path.Combine(root, result.Speakers[0].VoiceReference!.Path)));
        Assert.Equal("external.wav", result.Speakers[1].VoiceReference!.Path);
    }

    [RuntimeFact] public async Task InstalledQwenUsesCudaAutoAndCpuOnRequest()
    {
        Directory.CreateDirectory(root);
        var settings = new SettingsStore(Path.Combine(root, "settings.json"));
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Capture(string line) => lines.Enqueue(line);
        ActivityJournal.Entry += Capture;
        try
        {
            using var coordinator = new RuntimeCoordinator();
            using var engine = new LlamaCppTranslationEngine(settings, new RuntimeLocator(Environment.GetEnvironmentVariable("DUBBING_LLAMA_PACKAGE")), coordinator,
                new ProcessRunner(NullLogger<ProcessRunner>.Instance));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            foreach (string mode in new[] { "auto", "cpu", "cuda" })
            {
                await settings.SaveAsync(new() { ModelDirectory = Environment.GetEnvironmentVariable("DUBBING_MODELS")!, TranslationBackend = mode, Threads = 12 }, default);
                lines.Clear();
                var result = await engine.TranslateAsync(new("cuda-test", "We need to leave before the rain starts.", "en", "es"), timeout.Token);
                Assert.Equal("es", result.Language); Assert.False(string.IsNullOrWhiteSpace(result.Text));
                Assert.Contains(lines, l => l.Contains("selected=" + (mode == "cpu" ? "cpu" : "cuda")));
                if (Environment.GetEnvironmentVariable("DUBBING_LLAMA_TEST_LOG") is { Length: > 0 } log) await File.AppendAllLinesAsync(log, lines, timeout.Token);
                if (mode != "cpu") Assert.Contains(lines, l => l.Contains("offloaded") && !l.Contains("offloaded 0/"));

            }
            var cpuPath = new RuntimeLocator(Environment.GetEnvironmentVariable("DUBBING_LLAMA_PACKAGE")).Locate("llama-server");
            await settings.SaveAsync(settings.Current with { LlamaServerPath = cpuPath, TranslationBackend = "cuda" }, default);
            var unavailable = await Assert.ThrowsAsync<EngineException>(() => engine.TranslateAsync(new("no-cuda", "Hello", "en", "es"), timeout.Token));
            Assert.Contains("no CUDA devices", unavailable.Message);
            await settings.SaveAsync(settings.Current with { TranslationBackend = "auto" }, default);
            lines.Clear();
            var fallback = await engine.TranslateAsync(new("fallback", "Hello", "en", "es"), timeout.Token);
            Assert.Equal("es", fallback.Language); Assert.Contains(lines, l => l.Contains("selected=cpu"));
        }
        finally { ActivityJournal.Entry -= Capture; }
    }

    [RuntimeFact] public async Task InstalledDefaultQwenTranslatesWithoutConfiguredModelPath()
    {
        Directory.CreateDirectory(root);
        var settings = new SettingsStore(Path.Combine(root, "settings.json"));
        await settings.SaveAsync(new() { ModelDirectory = Environment.GetEnvironmentVariable("DUBBING_MODELS")!,
            LlamaServerPath = Environment.GetEnvironmentVariable("DUBBING_LLAMA_SERVER"), Backend = "cpu", Threads = 12 }, default);
        using var engine = new LlamaCppTranslationEngine(settings, new RuntimeLocator(), new RuntimeCoordinator(),
            new ProcessRunner(NullLogger<ProcessRunner>.Instance));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await engine.TranslateAsync(new("default-qwen-test", "We need to leave before the rain starts.", "en", "es"), timeout.Token);
        Assert.Equal("default-qwen-test", result.SegmentId);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Equal("es", result.Language);
    }

    [FFmpegFact] public async Task IndexMapsSpeedToNativeDurationFactorWithoutServerResampling()
    {
        Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "reference.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "fixture", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(2) }, null, default);
        var transport = new FakeTransport();
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media);
        foreach (double rate in new[] { .5, 1, 1.25, 2 })
        {
            await engine.SynthesizeAsync(new() { Text = "Hola", Language = "es", VoiceReference = new(audio), OutputPath = Path.Combine(root, "out.wav"),
                SpeakingRate = rate, EngineOptions = new Dictionary<string, object?> { ["duration_factor"] = 4 } }, null, default);
            Assert.False(transport.Body.TryGetProperty("speed", out _));
            Assert.Equal(1 / rate, transport.Body.GetProperty("options").GetProperty("duration_factor").GetDouble());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2.1)]
    [InlineData(double.NaN)]
    public async Task IndexRejectsInvalidSpeedBeforeInference(double speed)
    {
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), new FakeTransport(), Media);
        await Assert.ThrowsAsync<EngineException>(() => engine.SynthesizeAsync(new() { Text = "test", Language = "es", SpeakingRate = speed, OutputPath = "unused.wav" }, null, default));
    }

    [FFmpegFact] public async Task IndexForcesSupportedTargetLanguagesOverAdvancedOptions()
    {
        Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "reference.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "fixture", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(2) }, null, default);
        var transport = new FakeTransport();
        var model = Model(EngineTask.Tts);
        var engine = new AudioCppTtsEngine(model, transport, Media);
        foreach (var (target, expected) in new[] { ("zh", "zh"), ("en", "en"), ("ja", "ja"), ("es", "es"), ("ar", "ar"), ("ES", "es"), ("es-ES", "es"), (" es_MX ", "es") })
        {
            var request = new TtsRequest { Text = "Hola", Language = target, OutputPath = Path.Combine(root, "out.wav"), VoiceReference = new(audio),
                EngineOptions = new Dictionary<string, object?> { ["language"] = "en", ["lang"] = "EN", ["Language"] = "auto", ["temperature"] = .7 } };
            await engine.SynthesizeAsync(request, null, default);
            Assert.Equal(expected, transport.Body.GetProperty("language").GetString());
            var options = transport.Body.GetProperty("options");
            Assert.Equal(expected, options.GetProperty("language").GetString());
            Assert.False(options.TryGetProperty("lang", out _));
            Assert.False(options.TryGetProperty("Language", out _));
            Assert.Equal(.7, options.GetProperty("temperature").GetDouble());
            Assert.NotEqual(TtsCacheKey.Create(model.Descriptor, model.Id, null, request), TtsCacheKey.Create(engine.Descriptor, model.Id, null, request));
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("fr")]
    public async Task IndexRejectsUnsupportedOrAutomaticLanguage(string language)
    {
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), new FakeTransport(), Media);
        var error = await Assert.ThrowsAsync<EngineException>(() => engine.SynthesizeAsync(new() { Text = "test", Language = language, OutputPath = "unused.wav" }, null, default));
        Assert.Contains("target language", error.Message);
    }

    [FFmpegFact] public async Task IndexRejectsShortReferencesBeforeSendingToNativeRuntime()
    {
        Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "short.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "fixture", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(2) }, null, default);
        var transport = new FakeTransport { BeforeSpeech = _ => throw new Exception("Short references must not reach the native runtime.") };
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media);
        var request = new TtsRequest { Text = "Estoy", Language = "es", OutputPath = Path.Combine(root, "out.wav"), VoiceReference = new(audio, Start: TimeSpan.Zero, End: TimeSpan.FromMilliseconds(80)) };
        Assert.Contains("at least 1 second", (await Assert.ThrowsAsync<EngineException>(() => engine.SynthesizeAsync(request, null, default))).Message);
        request = request with { VoiceReference = new(audio), Emotion = new() { Mode = EmotionMode.ReferenceAudio, ReferenceAudio = new(audio, Start: TimeSpan.Zero, End: TimeSpan.FromMilliseconds(80)) } };
        Assert.Contains("at least 1 second", (await Assert.ThrowsAsync<EngineException>(() => engine.SynthesizeAsync(request, null, default))).Message);
        Assert.Empty(Directory.GetDirectories(root, "reference-*"));
    }

    [FFmpegFact] public async Task ExcludedSegmentsBypassTranslationSpeechRenderingAndSubtitles()
    {
        Directory.CreateDirectory(root);
        var project = new DubbingProject { Source = new("unused.mp4", "hash", TimeSpan.FromSeconds(2), 1, 24000),
            Settings = new() { BackgroundGain = 0 }, Engines = new() { Translation = "mock/translation" },
            Segments = [new() { Start = TimeSpan.Zero, End = TimeSpan.FromMilliseconds(80), ExcludedFromDubbing = true, Source = new("en", "Music") }] };
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        var store = new Dubbing.Infrastructure.Projects.JsonProjectStore(new Dubbing.Infrastructure.Projects.ProjectJson(), NullLogger<Dubbing.Infrastructure.Projects.JsonProjectStore>.Instance);
        var translated = await new TranslationService(registry).TranslateAsync(project, null, default);
        Assert.Equal("", translated.Segments[0].Translation.Text);
        var generated = await new GenerateSpeechService(new Dubbing.Application.Engines.EngineSelectionService(registry), store).GenerateAsync(translated, root, null, default);
        Assert.Null(generated.Segments[0].GeneratedSpeech);
        string mixed = await new DubRenderService(Media).RenderAsync(generated, root, null, default);
        Assert.True(File.Exists(mixed));
        string srt = Path.Combine(root, "out.srt"); await SubtitleExporter.WriteSrtAsync(generated, srt, default);
        Assert.Equal("", await File.ReadAllTextAsync(srt));
        await store.SaveAsync(generated, root, default);
        Assert.True((await store.LoadAsync(root, default)).Segments[0].ExcludedFromDubbing);
    }
    [InstalledIndexFact] public async Task InstalledIndexGeneratesSpanishWithOriginalVoiceAndEmotion()
    {
        Directory.CreateDirectory(root);
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var settings = new SettingsStore(Path.Combine(root, "settings.json"));
        await settings.SaveAsync(new() { ModelDirectory = Environment.GetEnvironmentVariable("DUBBING_INDEX_TEST_MODELS")!, AudioServerPath = Environment.GetEnvironmentVariable("DUBBING_AUDIO_SERVER"), Threads = 12 }, ct.Token);
        using var coordinator = new RuntimeCoordinator();
        using var transport = new AudioCppTransport(settings, Catalog, new RuntimeLocator(Environment.GetEnvironmentVariable("DUBBING_RUNTIME_TEST_ROOT")), runner, coordinator);
        var runtimeLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Record(string line) => runtimeLog.Enqueue(line);
        ActivityJournal.Entry += Record;
        try
        {
        string input = Environment.GetEnvironmentVariable("DUBBING_INDEX_TEST_AUDIO")!;
        var reference = new AudioReference(input, Start: TimeSpan.FromSeconds(60), End: TimeSpan.FromSeconds(65));
        var output = Path.Combine(AppContext.BaseDirectory, "index-source-spanish.wav");
        var result = await new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media).SynthesizeAsync(new()
        {
            Text = "He visto cosas que no creerías.", Language = "es", OutputPath = output, VoiceReference = reference, Seed = 42, SpeakingRate = 1,
            Emotion = new() { Mode = EmotionMode.SourceAudio, ReferenceAudio = reference, Intensity = 1 }
        }, null, ct.Token);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.True(new FileInfo(output).Length > 1000);
        var second = Path.Combine(AppContext.BaseDirectory, "index-source-spanish-second.wav");
        var faster = await new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media).SynthesizeAsync(new()
        { Text = "He visto cosas que no creerías.", Language = "es", OutputPath = second, VoiceReference = reference, Seed = 42, SpeakingRate = 1.25,
          Emotion = new() { Mode = EmotionMode.SourceAudio, ReferenceAudio = reference, Intensity = 1 } }, null, ct.Token);
        Assert.InRange(faster.Duration.TotalSeconds / result.Duration.TotalSeconds, .65, .95);
        runtimeLog.Enqueue($"Native speed validation: speed=1 duration={result.Duration.TotalSeconds:F3} s; speed=1.25 duration={faster.Duration.TotalSeconds:F3} s");
        Assert.Single(runtimeLog, line => line.StartsWith("Starting local runtime audiocpp_server.exe"));
        Assert.Contains(runtimeLog, line => line.Contains("reusing resident model"));
        await coordinator.ReleaseAsync();
        Assert.Contains(runtimeLog, line => line.Contains("unloaded resident models; server remains available"));
        }
        finally
        {
            ActivityJournal.Entry -= Record;
            await File.WriteAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "index-runtime-log.txt"), runtimeLog);
        }
    }
    private sealed class InstalledIndexFactAttribute : FactAttribute
    {
        public InstalledIndexFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DUBBING_INDEX_TEST_AUDIO") is null || Environment.GetEnvironmentVariable("DUBBING_INDEX_TEST_MODELS") is null)
                Skip = "Requires explicit installed IndexTTS models and a local source recording.";
        }
    }
    [FFmpegFact] public async Task IndexPipelineUsesSameOriginalClipForVoiceAndEmotionInTargetLanguage()
    {
        var media = Media;
        string source = Path.Combine(root, "assets", "extracted", "master.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "source", Language = "en", OutputPath = source, TargetDuration = TimeSpan.FromSeconds(4) }, null, default);
        var transport = new FakeTransport();
        transport.BeforeSpeech = async body =>
        {
            Assert.Equal("Hola", body.GetProperty("input").GetString());
            Assert.Equal("es", body.GetProperty("language").GetString());
            Assert.Equal("es", body.GetProperty("options").GetProperty("language").GetString());
            string voice = body.GetProperty("voice_ref").GetString()!, emotion = body.GetProperty("audio").GetString()!;
            Assert.Equal(await File.ReadAllBytesAsync(voice), await File.ReadAllBytesAsync(emotion));
            Assert.InRange((await media.ProbeAsync(voice, default)).Duration.TotalSeconds, .99, 1.01);
            Assert.Equal(1, body.GetProperty("options").GetProperty("emotion_alpha").GetDouble());
        };
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), transport, media);
        var registry = new EngineRegistry([engine]);
        var store = new Dubbing.Infrastructure.Projects.JsonProjectStore(new Dubbing.Infrastructure.Projects.ProjectJson(), NullLogger<Dubbing.Infrastructure.Projects.JsonProjectStore>.Instance);
        var project = new DubbingProject { Source = new(source, "source", TimeSpan.FromSeconds(4), 1, 24000), Engines = new() { Tts = engine.Descriptor.EngineId },
            Segments = [new() { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(3), Source = new("en", "Hello"), Translation = new("es", "Hola"), DirtyStages = [] }] };
        var result = await new GenerateSpeechService(new Dubbing.Application.Engines.EngineSelectionService(registry), store).GenerateAsync(project, root, null, default);
        Assert.NotNull(result.Segments[0].GeneratedSpeech);
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "dubbing-provider-" + Guid.NewGuid());
    [FFmpegFact] public async Task ImportedSpeakerVoiceOverridesSourceDefaultButKeepsOriginalEmotionAndChangesCacheKey()
    {
        string source = Path.Combine(root, "assets", "extracted", "master.wav");
        string voice = Path.Combine(root, "assets", "voices", "spanish.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "original", Language = "en", OutputPath = source, TargetDuration = TimeSpan.FromSeconds(4) }, null, default);
        await new MockTtsEngine(frequency: 880).SynthesizeAsync(new() { Text = "reference", Language = "es", OutputPath = voice, TargetDuration = TimeSpan.FromSeconds(3) }, null, default);
        bool expectManual = false;
        var transport = new FakeTransport { BeforeSpeech = async body =>
        {
            byte[] actualVoice = await File.ReadAllBytesAsync(body.GetProperty("voice_ref").GetString()!);
            byte[] actualEmotion = await File.ReadAllBytesAsync(body.GetProperty("audio").GetString()!);
            if (expectManual) { Assert.Equal(await File.ReadAllBytesAsync(voice), actualVoice); Assert.False(actualVoice.SequenceEqual(actualEmotion)); }
            else Assert.Equal(actualVoice, actualEmotion);
        } };
        var engine = new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media);
        var registry = new EngineRegistry([engine]);
        var store = new Dubbing.Infrastructure.Projects.JsonProjectStore(new Dubbing.Infrastructure.Projects.ProjectJson(), NullLogger<Dubbing.Infrastructure.Projects.JsonProjectStore>.Instance);
        var service = new GenerateSpeechService(new Dubbing.Application.Engines.EngineSelectionService(registry), store);
        var speaker = new SpeakerProfile { Id = "speaker-2", DisplayName = "Speaker 2", VoiceReference = new("assets/voices/spanish.wav"), VoiceReferenceIsAutomatic = true };
        var project = new DubbingProject { Source = new(source, "source", TimeSpan.FromSeconds(4), 1, 16000), Speakers = [speaker], Engines = new() { Tts = engine.Descriptor.EngineId },
            Segments = [new() { SpeakerId = speaker.Id, Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(3), Source = new("en", "Hello"), Translation = new("es", "Hola"), DirtyStages = [] }] };
        var original = await service.GenerateAsync(project, root, null, default);
        expectManual = true;
        foreach (bool? origin in new bool?[] { false, null })
        {
            var changed = original with { Speakers = [speaker with { VoiceReferenceIsAutomatic = origin }] };
            var generated = await service.GenerateAsync(changed, root, null, default);
            Assert.NotEqual(original.Segments[0].GeneratedSpeech!.CacheKey, generated.Segments[0].GeneratedSpeech!.CacheKey);
            Assert.True((await store.LoadAsync(root, default)).Speakers[0].HasManualVoiceReference);
        }
    }
    private readonly ProcessRunner runner = new(NullLogger<ProcessRunner>.Instance);
    private ModelCatalog Catalog => new(Path.Combine(AppContext.BaseDirectory, "engine-catalog"));
    private CatalogModel Model(EngineTask task) => Catalog.Models.Single(m => m.Provider == "audio.cpp" && m.Task == task);
    private FFmpegMediaEngine Media => new(runner, new RuntimeLocator(), new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG"), Environment.GetEnvironmentVariable("DUBBING_FFPROBE")));

    [Fact] public void CatalogHasPinnedFilesAndConservativeCapabilities()
    {
        Assert.Equal(6, Catalog.Models.Count);
        Assert.All(Catalog.Models, m => Assert.All(m.Files, f => { Assert.Equal(64, f.Sha256.Length); Assert.DoesNotContain("/main/", f.Url.ToString()); }));
        var tts = Model(EngineTask.Tts);
        Assert.True(tts.Capabilities.SupportsEmotionReferenceAudio);
        Assert.False(tts.Capabilities.SupportsTargetDuration);
        Assert.True(tts.Capabilities.SupportsSpeakingRate);
        Assert.Equal("audio", tts.FieldMappings["emotionAudio"]);
    }

    [FFmpegFact] public async Task AudioAdaptersNormalizeSampleOffsetsAndLanguageWithoutLosingWords()
    {
        var transport = new FakeTransport { Response = """{"text":"Hello.","language":"English","sample_rate":24000,"words":[{"word":"Hello.","start_sample":12000,"end_sample":36000,"confidence":0.9}],"segments":[{"text":"Hello.","start_sample":12000,"end_sample":36000}],"speaker_turns":[{"speaker_id":"speaker_0","start_sample":12000,"end_sample":36000}]}""" };
        Directory.CreateDirectory(root);
        var audio = Path.Combine(root, "audio.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "Hello", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(3) }, null, default);
        var result = await new AudioCppTranscriptionEngine(Model(EngineTask.Transcription), transport, Media).TranscribeAsync(new(new(audio), "en", TimeSpan.FromSeconds(3)), null, default);
        Assert.Equal("en", result.Language); Assert.Equal(TimeSpan.FromSeconds(.5), result.Words![0].Start);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Segments[0].End);
        Assert.Equal("English", transport.Body.GetProperty("language").GetString());
        var turns = await new AudioCppDiarizationEngine(Model(EngineTask.Diarization), transport).DiarizeAsync(new(new("audio.wav")), null, default);
        Assert.Equal("speaker_0", turns.Turns.Single().SpeakerId);
        var aligned = await new AudioCppAlignmentEngine(Model(EngineTask.Alignment), transport).AlignAsync(new(new("audio.wav"), "Hello.", "en"), default);
        Assert.Equal("v1/tasks/run", transport.Route); Assert.Equal(result.Words, aligned.Words);
    }

    [FFmpegFact] public async Task LongAudioPairsEachTranscriptWithItsAudioAndRestoresGlobalWordOffsets()
    {
        Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "long.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "Hello", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(31) }, null, default);
        var transport = new FakeTransport { Response = """{"text":"Hello","language":"English","words":[{"word":"Hello","start_sample":1600,"end_sample":8000}]}""" };
        var durations = new List<double>();
        transport.BeforeJson = async body =>
        {
            var request = body.TryGetProperty("request", out var nested) ? nested : body;
            durations.Add((await Media.ProbeAsync(request.GetProperty("audio").GetString()!, default)).Duration.TotalSeconds);
            if (body.TryGetProperty("request", out _)) Assert.Equal("Hello!", request.GetProperty("text").GetString());
        };
        var transcript = await new AudioCppTranscriptionEngine(Model(EngineTask.Transcription), transport, Media).TranscribeAsync(new(new(audio), "en", TimeSpan.FromSeconds(31)), null, default);
        Assert.Equal(new[] { 0d, 15d, 30d }, transcript.Segments.Select(s => s.Start.TotalSeconds));
        Assert.Equal(new[] { 15d, 30d, 31d }, transcript.Segments.Select(s => s.End.TotalSeconds));
        transcript = transcript with { Segments = transcript.Segments.Select(s => s with { Text = "Hello!" }).ToArray() };
        var aligned = await new ChunkAlignmentService(Media).AlignAsync(new AudioCppAlignmentEngine(Model(EngineTask.Alignment), transport), new(audio), transcript, "en", null, default);
        Assert.All(aligned.Words, word => Assert.Equal("Hello!", word.Text));
        Assert.Equal(new[] { .1, 15.1, 30.1 }, aligned.Words.Select(w => w.Start.TotalSeconds));
        Assert.Equal(new[] { 15d, 15d, 1d, 15d, 15d, 1d }, durations);
        Assert.False(File.Exists(transport.Body.GetProperty("request").GetProperty("audio").GetString()));
    }

    [FFmpegFact] public async Task CancelledTranscriptionRemovesTemporaryAudio()
    {
        Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "cancel.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "Hello", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
        using var cts = new CancellationTokenSource();
        var transport = new FakeTransport { BeforeJson = _ => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AudioCppTranscriptionEngine(Model(EngineTask.Transcription), transport, Media).TranscribeAsync(new(new(audio), "en", TimeSpan.FromSeconds(1)), null, cts.Token));
        Assert.False(File.Exists(transport.Body.GetProperty("audio").GetString()));
        Assert.True(File.Exists(audio));
    }

    [InstalledModelFact] public async Task InstalledModelsCanTranscribeAndAlignLongClip()
    {
        var endpoint = Environment.GetEnvironmentVariable("DUBBING_SMOKE_ENDPOINT");
        var input = Environment.GetEnvironmentVariable("DUBBING_SMOKE_AUDIO");
        using var transport = new InstalledTransport(endpoint!);
        var transcript = await new AudioCppTranscriptionEngine(Model(EngineTask.Transcription), transport, Media).TranscribeAsync(new(new(input!), "en"), null, default);
        Assert.True(transcript.Segments.Count > 1);
        var aligned = await new ChunkAlignmentService(Media).AlignAsync(new AudioCppAlignmentEngine(Model(EngineTask.Alignment), transport), new(input!), transcript, "en", null, default);
        Assert.NotEmpty(aligned.Words);
        Assert.Contains(aligned.Words, w => w.Start > TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "installed-model-smoke.json"), JsonSerializer.Serialize(new { transcript, aligned }));
    }

    private sealed class InstalledModelFactAttribute : FactAttribute
    {
        public InstalledModelFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FFMPEG_TESTS") != "1" ||
                Environment.GetEnvironmentVariable("DUBBING_SMOKE_ENDPOINT") is null ||
                Environment.GetEnvironmentVariable("DUBBING_SMOKE_AUDIO") is null)
                Skip = "Requires an explicitly selected running model server and long audio fixture.";
        }
    }

    private sealed class InstalledTransport(string endpoint) : IAudioCppTransport, IDisposable
    {
        private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(30) };
        public async Task<JsonElement> JsonAsync(CatalogModel model, string route, object request, CancellationToken ct)
        {
            using var response = await client.PostAsync(endpoint + "/" + route, new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"), ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.IsSuccessStatusCode, body);
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        public Task SpeechAsync(CatalogModel model, object request, string output, CancellationToken ct) => throw new NotSupportedException();
        public Task SeparateAsync(CatalogModel model, string input, string outputDirectory, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() => client.Dispose();
    }

    [Fact] public void TimelineSplitsAtSpeakerChangesAndSentenceBoundaries()
    {
        WordTiming[] words = [new("One.", TimeSpan.Zero, TimeSpan.FromSeconds(1)), new("Two", TimeSpan.FromSeconds(1.1), TimeSpan.FromSeconds(2)), new("three", TimeSpan.FromSeconds(2.1), TimeSpan.FromSeconds(3))];
        var segments = new TimelineBuilder().Build(new("One. Two three", "en", [], words), new([new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "a"), new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "b")]), "es");
        Assert.Equal(3, segments.Count); Assert.Equal("b", segments[2].SpeakerId); Assert.Equal(words[2], segments[2].Words.Single());
    }

    [Theory]
    [InlineData("wrong", "es", "Hola")]
    [InlineData("s1", "en", "Hola")]
    [InlineData("s1", "es", " ")]
    public void StructuredTranslationRejectsWrongIdentityLanguageOrEmptyText(string id, string language, string text)
    {
        var request = new TranslationRequest("s1", "Hello", "en", "es");
        Assert.Throws<EngineException>(() => LlamaCppTranslationEngine.Parse(JsonSerializer.Serialize(new { segmentId = id, language, translation = text }), request));
    }
    [Fact] public void StructuredTranslationAcceptsFencedJson()
    {
        var result = LlamaCppTranslationEngine.Parse("```json\n{\"segmentId\":\"s1\",\"language\":\"es\",\"translation\":\"Hola\"}\n```", new("s1", "Hello", "en", "es"));
        Assert.Equal("Hola", result.Text);
    }

    [Fact] public void LocalizationFallsBackToEnglishAndUpdatesExistingBindings()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "en.json"), "{\"Open project\":\"Open project\",\"Save\":\"Save\"}");
        File.WriteAllText(Path.Combine(root, "es.json"), "{\"Save\":\"Guardar\"}");
        var catalog = new TextCatalog(root); var value = catalog.For("Save"); catalog.Load("es");
        Assert.Equal("Guardar", value.Value); Assert.Equal("Open project", catalog["Open project"]);
        Assert.Throws<ArgumentException>(() => catalog.Load("../secret"));
    }

    [Theory] [InlineData("../escape")] [InlineData("a/../../escape")]
    public void CatalogPathsCannotEscapeRoot(string relative) => Assert.Throws<InvalidDataException>(() => ModelCatalog.SafePath(root, relative));

    [Fact] public async Task ModelDownloadVerifiesHashAndPreservesExistingFileOnBadResponse()
    {
        Directory.CreateDirectory(root); byte[] data = Encoding.UTF8.GetBytes("pinned model fixture");
        var model = Model(EngineTask.Transcription) with { EntryPoint = "test.gguf", Files = [new("test.gguf", new("https://example.invalid/model"), data.Length, Convert.ToHexStringLower(SHA256.HashData(data)))] };
        string catalogPath = Path.Combine(root, "catalog"); Directory.CreateDirectory(catalogPath);
        File.WriteAllText(Path.Combine(catalogPath, "models.json"), JsonSerializer.Serialize(new[] { model }, ModelCatalog.JsonOptions));
        var settings = new SettingsStore(Path.Combine(root, "settings.json")); await settings.SaveAsync(new() { ModelDirectory = Path.Combine(root, "models") }, default);
        var handler = new BytesHandler(data);
        using var manager = new VerifiedModelManager(new(catalogPath), settings, runner, new(handler));
        await manager.InstallAsync(model.Id, null, default); Assert.True(await manager.VerifyAsync(model.Id, default));
        string output = Path.Combine(settings.Current.ModelDirectory, "test.gguf");
        await File.WriteAllTextAsync(output, "previous file"); handler.Data = Enumerable.Repeat((byte)120, data.Length).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(model.Id, null, default));
        Assert.Equal("previous file", await File.ReadAllTextAsync(output)); Assert.Empty(Directory.GetFiles(settings.Current.ModelDirectory, "*.download-*"));
        await manager.RemoveAsync(model.Id, default); Assert.False(File.Exists(output));
    }

    [Fact] public async Task RuntimeCoordinatorStopsPreviousModelAndCancellationStopsCurrent()
    {
        using var coordinator = new RuntimeCoordinator(); int stops = 0;
        await coordinator.RunAsync("audio", () => stops++, () => Task.FromResult(true), default);
        await coordinator.RunAsync("llama", () => stops++, () => Task.FromResult(true), default);
        Assert.Equal(1, stops);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync("llama", () => stops++, async () => { cancellation.Cancel(); await Task.Delay(1, cancellation.Token); return true; }, cancellation.Token));
        Assert.Equal(2, stops);
    }

    [Fact] public async Task RuntimeResidencyReusesOneOwnerAndAwaitsReleaseBeforeSwitchOrCompletion()
    {
        using var coordinator = new RuntimeCoordinator();
        var calls = new List<string>();
        async Task Release() { await Task.Delay(5); calls.Add("released"); }
        for (int i = 0; i < 3; i++)
            await coordinator.RunAsync("audio", Release, () => { }, () => { calls.Add("tts"); return Task.FromResult(true); }, default);
        Assert.Equal(new[] { "tts", "tts", "tts" }, calls);
        await coordinator.RunAsync("translator", Release, () => { }, () => { calls.Add("translation"); return Task.FromResult(true); }, default);
        await coordinator.ReleaseAsync();
        Assert.Equal(new[] { "tts", "tts", "tts", "released", "translation", "released" }, calls);
        await coordinator.ReleaseAsync(); Assert.Equal(6, calls.Count);
    }

    [FFmpegFact] public async Task TtsSeparatesVoiceAndEmotionReferencesAndCutsRequestedRanges()
    {
        Directory.CreateDirectory(root); string voice = Path.Combine(root, "voice.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "reference", Language = "en", OutputPath = voice, TargetDuration = TimeSpan.FromSeconds(3) }, null, default);
        var transport = new FakeTransport();
        transport.BeforeSpeech = async body =>
        {
            string voicePath = body.GetProperty("voice_ref").GetString()!, emotionPath = body.GetProperty("audio").GetString()!;
            Assert.NotEqual(voicePath, emotionPath);
            Assert.InRange((await Media.ProbeAsync(voicePath, default)).Duration.TotalSeconds, .99, 1.01);
            Assert.InRange((await Media.ProbeAsync(emotionPath, default)).Duration.TotalSeconds, 1.49, 1.51);
            Assert.Equal(.65, body.GetProperty("options").GetProperty("emotion_alpha").GetDouble());
        };
        await new AudioCppTtsEngine(Model(EngineTask.Tts), transport, Media).SynthesizeAsync(new() { Text = "Hello", Language = "en", OutputPath = Path.Combine(root, "speech.wav"),
            VoiceReference = new(voice, Start: TimeSpan.Zero, End: TimeSpan.FromSeconds(1)), Emotion = new() { Mode = EmotionMode.ReferenceAudio, Intensity = .65, ReferenceAudio = new(voice, Start: TimeSpan.FromSeconds(1), End: TimeSpan.FromSeconds(2.5)) } }, null, default);
        Assert.Empty(Directory.GetDirectories(root, "reference-*"));
    }

    [FFmpegFact] public async Task LockedTranslationIsNeverRewrittenAndSmallTimingErrorUsesStretch()
    {
        Directory.CreateDirectory(root); string raw = Path.Combine(root, "raw.wav");
        var engine = new MockTtsEngine();
        await engine.SynthesizeAsync(new() { Text = "locked", Language = "en", OutputPath = raw, TargetDuration = TimeSpan.FromSeconds(2.08) }, null, default);
        var segment = new DubSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Translation = new("en", "locked", true) };
        var fit = await new DurationFitService(Media, new EngineRegistry(MockEngineCatalog.Create())).FitAsync(new() { Settings = new() { DurationTolerance = .01, MaximumTimeStretch = .05 } }, segment, engine,
            new() { Text = "locked", Language = "en", OutputPath = raw }, new("raw.wav", TimeSpan.FromSeconds(2.08), "fixture"), root, null, default);
        Assert.Equal("locked", fit.Segment.Translation.Text); Assert.Equal(0, fit.Retries); Assert.NotNull(fit.Fitted);
        Assert.InRange(fit.Fitted.Duration.TotalSeconds, 1.97, 2.03);
        var peaks = await Media.WaveformAsync(raw, 80, default); Assert.Equal(80, peaks.Count); Assert.Contains(peaks, p => p > .05f);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class BytesHandler(byte[] data) : HttpMessageHandler
    {
        public byte[] Data { get; set; } = data;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) });
    }
    private sealed class FakeTransport : IAudioCppTransport
    {
        public string Response { get; init; } = "{}";
        public JsonElement Body { get; private set; }
        public string? Route { get; private set; }
        public Func<JsonElement, Task>? BeforeSpeech { get; set; }
        public Func<JsonElement, Task>? BeforeJson { get; set; }
        public async Task<JsonElement> JsonAsync(CatalogModel model, string route, object request, CancellationToken ct)
        { Body = JsonSerializer.SerializeToElement(request); Route = route; if (BeforeJson is not null) await BeforeJson(Body); return JsonSerializer.Deserialize<JsonElement>(Response); }
        public async Task SpeechAsync(CatalogModel model, object request, string output, CancellationToken ct)
        { Body = JsonSerializer.SerializeToElement(request); if (BeforeSpeech is not null) await BeforeSpeech(Body); await new MockTtsEngine().SynthesizeAsync(new() { Text = "test", Language = "en", OutputPath = output, TargetDuration = TimeSpan.FromSeconds(1) }, null, ct); }
        public Task SeparateAsync(CatalogModel model, string input, string outputDirectory, CancellationToken ct) => throw new NotSupportedException();
    }
}
