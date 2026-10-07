using Dubbing.Application.Pipeline;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;

namespace Dubbing.Application.Tests;

public sealed class TranslationCheckpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOnLastSegmentPreservesCheckpointsAndResumesOnlyPending(bool cancel)
    {
        var engine = new Translator(cancel);
        var service = new TranslationService(new EngineRegistry([engine]));
        var project = new DubbingProject { Engines = new() { Translation = engine.Descriptor.EngineId },
            Segments = Enumerable.Range(1, 14).Select(i => new DubSegment { Id = i.ToString(), Source = new("en", "Hello"), DirtyStages = [PipelineStage.Translation] }).ToArray() };
        var snapshots = new List<DubbingProject>();
        Task Checkpoint(DubbingProject p) { snapshots.Add(p); return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<Exception>(() => service.TranslateAsync(project, null, default, Checkpoint));
        Assert.Equal(13, snapshots.Count);
        var saved = snapshots[^1];
        Assert.All(saved.Segments.Take(13), s => { Assert.Equal("Hola", s.Translation.Text); Assert.DoesNotContain(PipelineStage.Translation, s.DirtyStages); });
        Assert.Contains(PipelineStage.Translation, saved.Segments[13].DirtyStages);
        Assert.True(string.IsNullOrWhiteSpace(snapshots[0].Segments[1].Translation.Text));
        engine.Fail = false;
        engine.Calls.Clear();
        var complete = await service.TranslateAsync(saved, null, default, Checkpoint);
        Assert.Equal(new[] { "14" }, engine.Calls);
        Assert.All(complete.Segments, s => Assert.Equal("Hola", s.Translation.Text));
    }

    [Fact]
    public async Task SelectionAndOverwriteRespectUnselectedAndManualResults()
    {
        var engine = new Translator(false) { Fail = false };
        var service = new TranslationService(new EngineRegistry([engine]));
        var project = new DubbingProject { Engines = new() { Translation = engine.Descriptor.EngineId },
            Segments = Enumerable.Range(1, 3).Select(i => new DubSegment { Id = i.ToString(), Source = new("en", "Hello"), Translation = new("es", "Manual", true) }).ToArray() };
        var skipped = await service.TranslateAsync(project, null, default, segmentIds: new HashSet<string> { "2" }, overwriteExisting: false);
        Assert.Empty(engine.Calls);
        var replaced = await service.TranslateAsync(project, null, default, segmentIds: new HashSet<string> { "2" }, overwriteExisting: true);
        Assert.Equal(new[] { "2" }, engine.Calls);
        Assert.Equal("Hola", replaced.Segments[1].Translation.Text);
        Assert.Same(project.Segments[0], replaced.Segments[0]);
        Assert.Same(project.Segments[2], replaced.Segments[2]);
    }

    private sealed class Translator(bool cancel) : ITranslationEngine
    {
        public bool Fail = true;
        public List<string> Calls = [];
        public EngineDescriptor Descriptor => new MockTranslationEngine().Descriptor;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            Calls.Add(request.SegmentId);
            if (Fail && request.SegmentId == "14")
            {
                if (cancel) throw new OperationCanceledException();
                throw new EngineException("Invalid last translation");
            }
            return Task.FromResult(new TranslationResult(request.SegmentId, "Hola", request.TargetLanguage));
        }
    }
}
