using Dubbing.Application.Engines;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;

namespace Dubbing.Application.Tests;

public sealed class PipelineTests
{
    [Theory]
    [InlineData(0, .08, 0, 1.2)]
    [InlineData(3.52, 3.76, 3.04, 4.24)]
    [InlineData(9.92, 10, 8.8, 10)]
    [InlineData(2, 5, 2, 5)]
    public void ShortSourceReferencesUseContextWithoutMovingSegment(double start, double end, double expectedStart, double expectedEnd)
    {
        var project = new DubbingProject { Source = new("source", "hash", TimeSpan.FromSeconds(10), 1, 16000) };
        var segment = new DubSegment { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) };
        var reference = GenerateSpeechService.SourceReference(project, segment, "master.wav");
        Assert.Equal(expectedStart, reference.Start!.Value.TotalSeconds, 6);
        Assert.Equal(expectedEnd, reference.End!.Value.TotalSeconds, 6);
        Assert.Equal(start, segment.Start.TotalSeconds, 6);
    }
    [Fact] public async Task LockedTranslationClearsInvalidationWithoutLosingManualText()
    {
        var project = new ProjectFactory(registry).Create("mock") with { Segments = [new() { Translation = new("es", "Manual", true) }] };
        project = ProjectEditor.Invalidate(project, PipelineStage.Translation);
        var result = await new TranslationService(registry).TranslateAsync(project, null, default);
        Assert.Equal("Manual", result.Segments[0].Translation.Text);
        Assert.DoesNotContain(PipelineStage.Translation, result.Segments[0].DirtyStages);
    }
    private readonly EngineRegistry registry = new(MockEngineCatalog.Create());

    [Fact] public void CapabilityFilteringAndOverridePrecedence()
    {
        var selector = new EngineSelectionService(registry);
        Assert.Empty(selector.Find(EngineTask.Tts, "es", c => c.SupportsVoiceCloning));
        Assert.Equal(2, selector.Find(EngineTask.Tts, "es", c => c.SupportsTargetDuration).Count);
        var project = new DubbingProject { Engines = new() { Tts = "mock/tone" }, Speakers = [new() { Id = "a", PreferredTtsEngine = "mock/tone-alt" }] };
        var segment = new DubSegment { SpeakerId = "a" };
        Assert.Equal("mock/tone-alt", selector.ResolveTts(project, segment).Descriptor.EngineId);
        Assert.Equal("mock/tone", selector.ResolveTts(project, segment with { Tts = new() { EngineId = "mock/tone" } }).Descriptor.EngineId);
        Assert.Equal("mock/tone", selector.ResolveTts(project, segment with { SpeakerId = null }).Descriptor.EngineId);
    }

    [Fact] public void TranslationEditInvalidatesOnlyDependentStagesOfEditedSegment()
    {
        var untouched = new DubSegment { Id = "second", DirtyStages = [] };
        var project = new DubbingProject { Segments = [new() { Id = "first", DirtyStages = [] }, untouched] };
        var edited = ProjectEditor.EditTranslation(project, "first", "Texto corregido");
        Assert.Equal(new[] { PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux }, edited.Segments[0].DirtyStages);
        Assert.True(edited.Segments[0].Translation.Locked);
        Assert.Same(untouched, edited.Segments[1]);
        Assert.DoesNotContain(PipelineStage.Transcription, StageDependencies.After(PipelineStage.Translation));
    }

    [Fact] public async Task GlobalTranslationPreservesManualEdits()
    {
        var project = new ProjectFactory(registry).Create() with
        {
            Segments = [new() { Id = "locked", Translation = new("es", "Mi edición", true) },
                new() { Id = "pending", Source = new("en", "Hello, welcome to the dubbing studio.") }]
        };
        var translated = await new TranslationService(registry).TranslateAsync(project, null, CancellationToken.None);
        Assert.Equal("Mi edición", translated.Segments[0].Translation.Text);
        Assert.Equal("Hola, bienvenido al estudio de doblaje.", translated.Segments[1].Translation.Text);
    }

    [Fact] public void TimelineUsesMaximumOverlapAndRetainsSilenceAndOverlap()
    {
        var transcription = new TranscriptionResult("one two", "en", [new("one", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)),
            new("two", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6))]);
        var turns = new DiarizationResult([new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "a", 1),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), "b", 0.9f)]);
        var result = new TimelineBuilder().Build(transcription, turns, "es");
        Assert.All(result, x => Assert.Equal("b", x.SpeakerId));
        Assert.Equal(TimeSpan.FromSeconds(2), result[0].Start);
        Assert.True(result[1].Start < result[0].End);
    }

    [Fact] public void CacheKeyIsCanonicalAndIncludesVoiceEmotionModelAndTiming()
    {
        var descriptor = new MockTtsEngine().Descriptor;
        var request = new TtsRequest { Text = "Hola", Language = "es", OutputPath = "one.wav",
            EngineOptions = new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 } };
        string Key(TtsRequest r, string version = "v1") => TtsCacheKey.Create(descriptor, "model", version, r);
        Assert.Equal(Key(request), Key(request with { OutputPath = "two.wav", EngineOptions = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 } }));
        Assert.NotEqual(Key(request), Key(request with { Text = "Adiós" }));
        Assert.NotEqual(Key(request), Key(request with { VoiceReference = new("voice.wav", "hash") }));
        Assert.NotEqual(Key(request), Key(request with { Emotion = new() { Intensity = 0.2 } }));
        Assert.NotEqual(Key(request), Key(request with { TargetDuration = TimeSpan.FromSeconds(2) }));
        Assert.NotEqual(Key(request), Key(request, "v2"));
    }

    [Fact] public async Task MockTtsWritesValidWavAndHonorsCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dubbing-tests-" + Guid.NewGuid());
        try
        {
            var request = new TtsRequest { Text = "Test", Language = "en", OutputPath = Path.Combine(directory, "tone.wav"), TargetDuration = TimeSpan.FromMilliseconds(200) };
            var engine = new MockTtsEngine();
            var result = await engine.SynthesizeAsync(request, null, CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(result.Audio.Path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(44 + 6400, bytes.Length);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SynthesizeAsync(request with { OutputPath = Path.Combine(directory, "canceled.wav") }, null, canceled.Token));
            Assert.False(File.Exists(Path.Combine(directory, "canceled.wav")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
