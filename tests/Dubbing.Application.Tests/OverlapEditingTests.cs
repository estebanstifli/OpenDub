using Dubbing.Application.Projects;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Projects;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Infrastructure.Engines;

namespace Dubbing.Application.Tests;

public sealed class OverlapEditingTests
{
    [Fact]
    public void TimingEditsPreserveSourceReferenceAndTextEditsPreserveLastFinishedAudio()
    {
        var a = new DubSegment { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4), Source = new("en", "Hello"), Translation = new("es", "Hola"),
            GeneratedSpeech = new("raw.wav", TimeSpan.FromSeconds(3), "raw"), FittedSpeech = new("fit.wav", TimeSpan.FromSeconds(2), "fit"), DirtyStages = [] };
        var b = new DubSegment { Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(6) };
        var project = new DubbingProject { Source = new("video", "", TimeSpan.FromSeconds(8), 2, 48000), Segments = [a, b] };
        var edit = OverlapEditing.Edit(project, a.Id, 2.1, 4.1, "Hello", "Hola");
        Assert.Equal(a.Start, edit.Segments[0].SourceReferenceStart); Assert.Equal(a.End, edit.Segments[0].SourceReferenceEnd);
        Assert.Equal(a.FittedSpeech, edit.Segments[0].FittedSpeech); Assert.Same(b, edit.Segments[1]);
        Assert.DoesNotContain(PipelineStage.Tts, edit.Segments[0].DirtyStages);
        edit = OverlapEditing.Edit(project, a.Id, 2, 4, "Hello", "Buenas");
        Assert.Equal(a.FittedSpeech, ProjectAudioMix.EffectiveSpeech(edit.Segments[0]));
        Assert.True(edit.Segments[0].Translation.Locked); Assert.DoesNotContain(PipelineStage.Translation, edit.Segments[0].DirtyStages);
        Assert.Throws<InvalidOperationException>(() => OverlapEditing.Edit(project, a.Id, 2, 9, "", ""));
        Assert.Throws<InvalidOperationException>(() => OverlapEditing.Edit(project, a.Id, 2, 2, "", ""));
        var merged = OverlapEditing.Merge(project, a.Id, b.Id);
        Assert.Single(merged.Segments); Assert.Equal(a.Id, merged.Segments[0].Id);
        Assert.Null(merged.Segments[0].GeneratedSpeech);
    }
    [Fact]
    public async Task ProjectPromptReachesTranslatorAndProtocolRemainsRequired()
    {
        var engine = new CaptureTranslator(); var registry = new EngineRegistry([engine]);
        var project = new DubbingProject { Engines = new() { Translation = engine.Descriptor.EngineId },
            Settings = new() { TranslationPrompt = "Use Spanish from Spain." },
            Segments = [new() { End = TimeSpan.FromSeconds(2), Source = new("en", "Hello") }] };
        await new TranslationService(registry).TranslateAsync(project, null, default);
        Assert.Equal(project.Settings.TranslationPrompt, engine.Request!.Prompt);
        Assert.Contains("Spanish from Spain", TranslationPrompts.Build(engine.Request.Prompt));
        Assert.Contains("Copy segmentId exactly", TranslationPrompts.Build(engine.Request.Prompt));
        Assert.StartsWith(TranslationPrompts.Default, TranslationPrompts.Build(" "));
        Assert.Equal(project.Settings.TranslationPrompt, System.Text.Json.JsonSerializer.Deserialize<DubbingProject>(System.Text.Json.JsonSerializer.Serialize(project))!.Settings.TranslationPrompt);
    }
    private sealed class CaptureTranslator : ITranslationEngine
    {
        public TranslationRequest? Request;
        public EngineDescriptor Descriptor => new Dubbing.Engines.Mock.MockTranslationEngine().Descriptor;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        { Request = request; return Task.FromResult(new TranslationResult(request.SegmentId, "Hola", request.TargetLanguage)); }
    }
}
