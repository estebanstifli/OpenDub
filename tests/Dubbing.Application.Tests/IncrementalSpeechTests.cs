using Dubbing.Application.Engines;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;

namespace Dubbing.Application.Tests;

public sealed class IncrementalSpeechTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("failure")]
    public async Task NativeBatchFinishesAndCheckpointsEachSegmentBeforeStartingNext(string outcome)
    {
        string root = Path.Combine(Path.GetTempPath(), "incremental-speech-" + Guid.NewGuid());
        var store = new Store(); using var cancel = new CancellationTokenSource();
        var requests = new List<string>(); var fractions = new List<double>();
        var project = new DubbingProject { Engines = new() { Tts = "mock/tone" },
            Segments = Enumerable.Range(1, 3).Select(i => new DubSegment { Id = i.ToString(), Start = TimeSpan.FromSeconds(i * 3), End = TimeSpan.FromSeconds(i * 3 + 2.5),
                Translation = new("es", i.ToString()), GeneratedSpeech = new($"old{i}.wav", TimeSpan.FromSeconds(2.5), "old"), DirtyStages = [] }).ToArray() };
        var engine = new Speech(async (request, ct) =>
        {
            requests.Add(request.Text);
            if (request.Text == "1") Assert.Empty(store.Snapshots);
            else
            {
                var checkpoint = store.Snapshots.Last();
                Assert.Equal(TimeSpan.FromSeconds(2.5), checkpoint.Segments[0].GeneratedSpeech!.Duration);
                Assert.True(File.Exists(Path.Combine(root, checkpoint.Segments[0].GeneratedSpeech!.File)));
                Assert.Equal(1.2, checkpoint.Segments[0].Tts.SpeakingRate);
                if (request.Text == "2" && outcome == "cancel") { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
                if (request.Text == "2" && outcome == "failure") throw new IOException("Second segment failed.");
            }
            await Task.Delay(15, ct); // Metrics must include both inference calls.
        });
        var registry = new EngineRegistry([engine]);
        var service = new GenerateSpeechService(new EngineSelectionService(registry), store, new DurationFitService(null!, registry));
        try
        {
            Task<DubbingProject> Run() => service.GenerateAsync(project, root, new Progress(p => fractions.Add(p.Fraction)), cancel.Token, overwriteExisting: true);
            if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else if (outcome == "failure") await Assert.ThrowsAsync<IOException>(Run);
            else
            {
                var batch = await Run();
                Assert.Equal(new[] { "1", "1", "2", "2", "3", "3" }, requests);
                Assert.Equal(3, store.Snapshots.Count);
                Assert.All(batch.Metrics, m => { Assert.Equal(1, m.Retries); Assert.True(m.WallSeconds >= .02); });
                var singleStore = new Store(); var singleEngine = new Speech((_, _) => Task.CompletedTask);
                var singleRegistry = new EngineRegistry([singleEngine]);
                var single = await new GenerateSpeechService(new EngineSelectionService(singleRegistry), singleStore, new DurationFitService(null!, singleRegistry))
                    .GenerateAsync(project, root, null, default, segmentId: "2", overwriteExisting: true);
                Assert.Equal(batch.Segments[1].GeneratedSpeech!.Duration, single.Segments[1].GeneratedSpeech!.Duration);
                Assert.Equal(batch.Segments[1].Tts.SpeakingRate, single.Segments[1].Tts.SpeakingRate);
                Assert.Equal(batch.Segments[1].Translation, single.Segments[1].Translation);
                Assert.Equal(1, fractions.Last());
            }
            if (outcome != "success")
            {
                Assert.Equal(new[] { "1", "1", "2" }, requests);
                Assert.Single(store.Snapshots);
                Assert.Same(project.Segments[1], store.Snapshots[0].Segments[1]);
                Assert.Same(project.Segments[2], store.Snapshots[0].Segments[2]);
                Assert.True(fractions.Last() < 1);
            }
            Assert.True(fractions.Zip(fractions.Skip(1), (a, b) => b >= a).All(x => x));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache", "tts-work"), "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReferenceHashesAreReusedWithinOperationButRefreshedForNextOperation()
    {
        string root = Path.Combine(Path.GetTempPath(), "reference-hashes-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.wav"); await File.WriteAllTextAsync(source, "reference");
        var hashes = new List<string>(); var log = new List<string>();
        var engine = new Speech((request, _) => { hashes.Add(request.VoiceReference!.Fingerprint!); Assert.Equal(request.VoiceReference.Fingerprint, request.Emotion!.ReferenceAudio!.Fingerprint); return Task.CompletedTask; });
        var registry = new EngineRegistry([engine]);
        var service = new GenerateSpeechService(new EngineSelectionService(registry), new Store());
        var project = new DubbingProject { Engines = new() { Tts = "mock/tone" },
            Speakers = [new() { Id = "speaker", VoiceReference = new(source) }],
            Segments = Enumerable.Range(0, 3).Select(i => new DubSegment { SpeakerId = "speaker", End = TimeSpan.FromSeconds(3), Translation = new("es", "test"),
                Emotion = new() { Mode = EmotionMode.ReferenceAudio, ReferenceAudio = new(source) } }).ToArray() };
        try
        {
            await service.GenerateAsync(project, root, new Progress(p => log.Add(p.Message)), default, fitDuration: false);
            Assert.Contains(log, s => s.Contains("computed 1, reused 5")); Assert.Single(hashes.Distinct());
            string old = hashes[0]; hashes.Clear();
            await File.WriteAllTextAsync(source, "changed reference");
            await service.GenerateAsync(project, root, null, default, fitDuration: false);
            Assert.All(hashes, h => Assert.NotEqual(old, h));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Progress(Action<EngineProgress> report) : IProgress<EngineProgress> { public void Report(EngineProgress value) => report(value); }
    private sealed class Speech(Func<TtsRequest, CancellationToken, Task> before) : ITtsEngine
    {
        public EngineDescriptor Descriptor => new MockTtsEngine().Descriptor with { Capabilities = new() { SupportsSpeakingRate = true, SupportsVoiceCloning = true, SupportsEmotionReferenceAudio = true } };
        public async Task<TtsResult> SynthesizeAsync(TtsRequest request, IProgress<EngineProgress>? progress, CancellationToken ct)
        {
            await before(request, ct);
            return await new MockTtsEngine().SynthesizeAsync(request with { TargetDuration = TimeSpan.FromSeconds(3 / (request.SpeakingRate ?? 1)) }, progress, ct);
        }
    }
    private sealed class Store : IProjectStore
    {
        public List<DubbingProject> Snapshots { get; } = [];
        public Task SaveAsync(DubbingProject project, string directory, CancellationToken ct) { Snapshots.Add(project); return Task.CompletedTask; }
        public Task<DubbingProject> LoadAsync(string directory, CancellationToken ct) => throw new NotSupportedException();
    }
}
