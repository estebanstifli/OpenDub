using Dubbing.Application.Engines;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;
using Dubbing.Infrastructure.Processes;
using Dubbing.Infrastructure.Projects;
using Dubbing.Media.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Integration.Tests;

public sealed class SpeechPublicationTests
{
    [FFmpegFact]
    public async Task OnlyFinalFittedAudioIsPublishedAndInterruptedAttemptsPreservePreviousAudio()
    {
        foreach (string outcome in new[] { "success", "cancel", "failure" })
        {
            string root = Path.Combine(Path.GetTempPath(), "speech-publication-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
                var old = new DubSegment { End = TimeSpan.FromSeconds(2), Source = new("en", "Original"), Translation = new("es", "Anterior"),
                    GeneratedSpeech = new("old.wav", TimeSpan.FromSeconds(2), "old", "old"), DirtyStages = [] };
                await new MockTtsEngine().SynthesizeAsync(new() { Text = "old", Language = "es", OutputPath = Path.Combine(root, "old.wav"), TargetDuration = TimeSpan.FromSeconds(2) }, null, default);
                var next = new DubSegment { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4) };
                var project = new DubbingProject { Segments = [old, next], Engines = new() { Tts = "mock/tone", Translation = "mock/translation" },
                    Settings = new() { DurationTolerance = .01, MaximumTtsRetries = 1, MaximumTimeStretch = .05 } };
                await store.SaveAsync(project, root, default);
                string before = await File.ReadAllTextAsync(Path.Combine(root, "project.json"));
                byte[] oldWave = await File.ReadAllBytesAsync(Path.Combine(root, "old.wav"));
                var engine = new CandidateSpeech(async () => Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "project.json"))));
                var translator = new Rewrite(); var registry = new EngineRegistry([engine, translator]);
                var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
                var media = new FFmpegMediaEngine(runner, new RuntimeLocator(), new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG"), Environment.GetEnvironmentVariable("DUBBING_FFPROBE")));
                var service = new GenerateSpeechService(new EngineSelectionService(registry), store, new DurationFitService(media, registry));
                using var cancel = new CancellationTokenSource();
                bool finalPhaseReached = false;
                var log = new InlineProgress(p =>
                {
                    if (!p.Message.Contains("Final duration pass:")) return;
                    finalPhaseReached = true;
                    Assert.Equal(before, File.ReadAllText(Path.Combine(root, "project.json")));
                    if (outcome == "cancel") { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); }
                    if (outcome == "failure") throw new IOException("Simulated failure before final stretch.");
                });
                Task<DubbingProject> Run() => service.GenerateAsync(project, root, log, cancel.Token, old.Id, true, overwriteExisting: true);
                if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
                else if (outcome == "failure") await Assert.ThrowsAsync<IOException>(Run);
                else
                {
                    var completed = await Run();
                    var segment = completed.Segments[0];
                    Assert.Equal("Adaptada", segment.Translation.Text);
                    Assert.NotNull(segment.FittedSpeech);
                    Assert.InRange(segment.FittedSpeech.Duration.TotalSeconds, 1.99, 2.01);
                    Assert.Null(SegmentCollisions.Find(completed));
                    Assert.NotNull(SegmentCollisions.Find(completed with { Segments = [segment with { FittedSpeech = null }, next] }));
                    Assert.StartsWith("generated/tts/", segment.GeneratedSpeech!.File);
                    Assert.StartsWith("generated/tts/", segment.FittedSpeech.File);
                    Assert.True(File.Exists(Path.Combine(root, segment.FittedSpeech.File)));
                    Assert.Equal(segment.FittedSpeech, (await store.LoadAsync(root, default)).Segments[0].FittedSpeech);
                    Assert.Same(next, completed.Segments[1]);
                }
                Assert.True(finalPhaseReached); Assert.Equal(2, engine.Calls); Assert.Equal(1, translator.Calls);
                Assert.Equal(oldWave, await File.ReadAllBytesAsync(Path.Combine(root, "old.wav")));
                if (outcome != "success") Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "project.json")));
                Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache", "tts-work"), "*", SearchOption.AllDirectories));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private sealed class InlineProgress(Action<EngineProgress> report) : IProgress<EngineProgress> { public void Report(EngineProgress value) => report(value); }
    private sealed class CandidateSpeech(Func<Task> check) : ITtsEngine
    {
        public int Calls;
        public EngineDescriptor Descriptor => new MockTtsEngine().Descriptor with { Capabilities = new() };
        public async Task<TtsResult> SynthesizeAsync(TtsRequest request, IProgress<EngineProgress>? progress, CancellationToken ct)
        {
            await check(); Calls++;
            Assert.Contains("cache/tts-work/", request.OutputPath.Replace('\\', '/'));
            return await new MockTtsEngine().SynthesizeAsync(request with { TargetDuration = TimeSpan.FromSeconds(Calls == 1 ? 3 : 2.08) }, progress, ct);
        }
    }
    private sealed class Rewrite : ITranslationEngine
    {
        public int Calls;
        public EngineDescriptor Descriptor => new MockTranslationEngine().Descriptor;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new TranslationResult(request.SegmentId, "Adaptada", request.TargetLanguage)); }
    }
}
