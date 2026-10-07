using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class TranslationService(IEngineRegistry registry)
{
    public async Task<DubbingProject> TranslateAsync(DubbingProject project, IProgress<EngineProgress>? progress, CancellationToken cancellationToken,
        Func<DubbingProject, Task>? checkpoint = null, IReadOnlySet<string>? segmentIds = null, bool? overwriteExisting = null)
    {
        var engine = registry.GetEngine<ITranslationEngine>(project.Engines.Translation
            ?? throw new InvalidOperationException("Select a translation engine."));
        var result = project.Segments.ToArray();
        for (int index = 0; index < result.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = result[index];
            if (segment.ExcludedFromDubbing || (segmentIds is not null && !segmentIds.Contains(segment.Id))) continue;
            if (overwriteExisting == false && !string.IsNullOrWhiteSpace(segment.Translation.Text)) continue;
            if (segment.Translation.Locked && overwriteExisting != true)
                result[index] = segment with { DirtyStages = segment.DirtyStages.Where(s => s != PipelineStage.Translation).ToArray() };
            if (overwriteExisting == true || (!segment.Translation.Locked && (string.IsNullOrWhiteSpace(segment.Translation.Text) || segment.DirtyStages.Contains(PipelineStage.Translation))))
            {
                progress?.Report(new((double)index / result.Length, $"Translating segment {index + 1}/{result.Length} with {engine.Descriptor.DisplayName}", segment.Id));
                var translation = await engine.TranslateAsync(new(segment.Id, segment.Source.Text,
                    segment.Source.Language, project.Settings.TargetLanguage)
                {
                    PreviousSegments = project.Segments.Skip(Math.Max(0, index - 2)).Take(Math.Min(index, 2)).Select(x => x.Source.Text).ToArray(),
                    ModelId = project.Settings.TranslationModelId,
                    NextSegments = project.Segments.Skip(index + 1).Take(2).Select(x => x.Source.Text).ToArray(),
                    Glossary = project.Settings.Glossary, SpeakerId = segment.SpeakerId, TargetDuration = segment.TargetDuration
                }, cancellationToken);
                if (translation.SegmentId != segment.Id || string.IsNullOrWhiteSpace(translation.Text) || translation.Language != project.Settings.TargetLanguage)
                    throw new Dubbing.Core.EngineException("The engine returned an invalid translation.");
                result[index] = segment with
                {
                    Translation = new(translation.Language, translation.Text), Status = SegmentStatus.Translated,
                    DirtyStages = segment.DirtyStages.Where(s => s != PipelineStage.Translation)
                        .Union(StageDependencies.After(PipelineStage.Translation)).Order().ToArray()
                };
            }
            if (checkpoint is not null && !ReferenceEquals(result[index], segment))
                await checkpoint(project with { Segments = result.ToArray() });
            progress?.Report(new((double)(index + 1) / result.Length, "Translating segments", segment.Id));
        }
        return project with { Segments = result };
    }
}
