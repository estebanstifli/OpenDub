using Dubbing.Application.Engines;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;

namespace Dubbing.Application.Tests;

public sealed class BatchResidencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GeneratesWholeTtsPassAndOnlyRewritesWhenDurationFitIsRequested(bool fitDuration)
    {
        string root = Path.Combine(Path.GetTempPath(), "batch-residency-" + Guid.NewGuid());
        var calls = new List<string>();
        var registry = new EngineRegistry([new Speech(calls), new Translator(calls)]);
        var store = new Store();
        var service = new GenerateSpeechService(new EngineSelectionService(registry), store, new DurationFitService(null!, registry));
        var project = new DubbingProject { Engines = new() { Tts = "mock/tone", Translation = "mock/translation" },
            Settings = new() { MaximumTtsRetries = 2, MaximumTranslationRetries = 2 },
            Segments = Enumerable.Range(0, 3).Select(i => new DubSegment { Id = i.ToString(), Start = TimeSpan.FromSeconds(i * 2), End = TimeSpan.FromSeconds(i * 2 + 2),
                Source = new("en", "original"), Translation = new("es", "long wording"), DirtyStages = [PipelineStage.Tts] }).ToArray() };
        try
        {
            var result = await service.GenerateAsync(project, root, null, default, fitDuration: fitDuration);
            Assert.Equal(fitDuration ? new[] { "tts", "tts", "tts", "translate", "translate", "translate", "tts", "tts", "tts" } : new[] { "tts", "tts", "tts" }, calls);
            Assert.All(result.Segments, s => {
                Assert.Equal(fitDuration ? "short" : "long wording", s.Translation.Text);
                Assert.Equal(TimeSpan.FromSeconds(fitDuration ? 2 : 3), s.GeneratedSpeech!.Duration);
                Assert.Equal(!fitDuration, s.DirtyStages.Contains(PipelineStage.DurationFit));
            });
            Assert.Equal(3, store.Writes); // Publish each completed segment, never retry candidates.
            if (fitDuration) Assert.All(store.Snapshots.SelectMany(p => p.Segments).Where(s => s.GeneratedSpeech is not null),
                s => Assert.Equal(TimeSpan.FromSeconds(2), s.GeneratedSpeech!.Duration));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache", "tts-work"), "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task NativeSpeedFitsWithoutTranslationRewrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-speed-" + Guid.NewGuid());
        var calls = new List<string>();
        var registry = new EngineRegistry([new Speech(calls, true), new Translator(calls)]);
        var service = new GenerateSpeechService(new EngineSelectionService(registry), new Store(), new DurationFitService(null!, registry));
        var project = new DubbingProject { Engines = new() { Tts = "mock/tone", Translation = "mock/translation" },
            Settings = new() { MaximumTtsRetries = 2, MaximumTranslationRetries = 2 },
            Segments = [new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2.5), Source = new("en", "original"), Translation = new("es", "long wording"), DirtyStages = [PipelineStage.Tts] }] };
        try
        {
            var result = await service.GenerateAsync(project, root, null, default);
            Assert.Equal(new[] { "tts", "tts" }, calls);
            Assert.Equal("long wording", result.Segments[0].Translation.Text);
            Assert.Equal(1.2, result.Segments[0].Tts.SpeakingRate);
            Assert.Equal(TimeSpan.FromSeconds(2.5), result.Segments[0].GeneratedSpeech!.Duration);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OverwriteRegeneratesOnlySelectedAudioWithoutChangingSharedOldFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "selected-tts-" + Guid.NewGuid());
        var calls = new List<string>();
        var registry = new EngineRegistry([new Speech(calls)]);
        var service = new GenerateSpeechService(new EngineSelectionService(registry), new Store());
        var project = new DubbingProject { Engines = new() { Tts = "mock/tone" },
            Segments = Enumerable.Range(1, 3).Select(i => new DubSegment { Id = i.ToString(), Start = TimeSpan.FromSeconds(i*3), End = TimeSpan.FromSeconds(i*3+3), Translation = new("es", "same text"), DirtyStages = [] }).ToArray() };
        try
        {
            project = await service.GenerateAsync(project, root, null, default, fitDuration: false);
            calls.Clear();
            var skipped = await service.GenerateAsync(project, root, null, default, fitDuration: false, segmentIds: new HashSet<string> { "2" }, overwriteExisting: false);
            Assert.Empty(calls);
            var oldPath = Path.Combine(root, project.Segments[0].GeneratedSpeech!.File);
            var bytes = await File.ReadAllBytesAsync(oldPath);
            var result = await service.GenerateAsync(project, root, null, default, fitDuration: false, segmentIds: new HashSet<string> { "2" }, overwriteExisting: true);
            Assert.Single(calls);
            Assert.Same(project.Segments[0], result.Segments[0]);
            Assert.Same(project.Segments[2], result.Segments[2]);
            Assert.NotEqual(project.Segments[1].GeneratedSpeech!.File, result.Segments[1].GeneratedSpeech!.File);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(oldPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Speech(List<string> calls, bool supportsSpeed = false) : ITtsEngine
    {
        public EngineDescriptor Descriptor => new MockTtsEngine().Descriptor with { Capabilities = new() { SupportsSpeakingRate = supportsSpeed } };
        public Task<TtsResult> SynthesizeAsync(TtsRequest request, IProgress<EngineProgress>? progress, CancellationToken ct)
        { calls.Add("tts"); return new MockTtsEngine().SynthesizeAsync(request with { TargetDuration = TimeSpan.FromSeconds((request.Text == "short" ? 2 : 3) / (request.SpeakingRate ?? 1)) }, progress, ct); }
    }
    private sealed class Translator(List<string> calls) : ITranslationEngine
    {
        public EngineDescriptor Descriptor => new MockTranslationEngine().Descriptor;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        { calls.Add("translate"); return Task.FromResult(new TranslationResult(request.SegmentId, "short", request.TargetLanguage)); }
    }
    private sealed class Store : IProjectStore
    {
        public int Writes;
        public List<DubbingProject> Snapshots { get; } = [];
        public Task SaveAsync(DubbingProject project, string directory, CancellationToken ct) { Writes++; Snapshots.Add(project); return Task.CompletedTask; }
        public Task<DubbingProject> LoadAsync(string directory, CancellationToken ct) => throw new NotSupportedException();
    }
}
