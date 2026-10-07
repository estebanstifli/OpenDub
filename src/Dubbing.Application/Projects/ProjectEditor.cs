using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Projects;

public static class ProjectEditor
{
    public static DubbingProject Invalidate(DubbingProject project, PipelineStage stage) => project with
    {
        Segments = project.Segments.Select(segment => segment with
        {
            DirtyStages = segment.DirtyStages.Append(stage).Union(StageDependencies.After(stage)).Order().ToArray(),
            Status = SegmentStatus.TtsDirty
        }).ToArray()
    };

    public static DubbingProject EditTranslation(DubbingProject project, string segmentId, string text)
    {
        if (!project.Segments.Any(x => x.Id == segmentId)) throw new KeyNotFoundException(segmentId);
        return project with
        {
            Segments = project.Segments.Select(segment => segment.Id != segmentId ? segment : segment with
            {
                Translation = new(project.Settings.TargetLanguage, text, Locked: true),
                Status = SegmentStatus.TranslationEdited,
                DirtyStages = segment.DirtyStages.Where(s => s != PipelineStage.Translation)
                    .Union(StageDependencies.After(PipelineStage.Translation)).Order().ToArray()
            }).ToArray()
        };
    }

    public static DubbingProject UnlockTranslation(DubbingProject project, string segmentId) => project with
    {
        Segments = project.Segments.Select(segment => segment.Id != segmentId ? segment : segment with
        {
            Translation = segment.Translation with { Locked = false },
            DirtyStages = segment.DirtyStages.Append(PipelineStage.Translation)
                .Union(StageDependencies.After(PipelineStage.Translation)).Order().ToArray()
        }).ToArray()
    };
}
