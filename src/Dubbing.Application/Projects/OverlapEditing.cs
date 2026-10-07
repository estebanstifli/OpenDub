using Dubbing.Application.Pipeline;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Projects;

public static class OverlapEditing
{
    public static DubbingProject Edit(DubbingProject project, string id, double start, double end, string original, string translation)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start ||
            project.Source is { } source && end > source.Duration.TotalSeconds)
            throw new InvalidOperationException("Segment range must stay inside the video.");
        var old = project.Segments.Single(s => s.Id == id);
        var updated = old with { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) };
        if (updated.Start != old.Start || updated.End != old.End)
            updated = updated with { SourceReferenceStart = old.SourceReferenceStart ?? old.Start, SourceReferenceEnd = old.SourceReferenceEnd ?? old.End,
                DirtyStages = old.DirtyStages.Union(updated.TargetDuration == old.TargetDuration
                    ? [PipelineStage.Mix, PipelineStage.Mux] : new[] { PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux }).ToArray() };
        if (original != old.Source.Text)
            updated = updated with { Source = old.Source with { Text = original }, Status = SegmentStatus.TtsDirty,
                DirtyStages = updated.DirtyStages.Union(new[] { PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux })
                    .Union(old.Translation.Locked ? [] : new[] { PipelineStage.Translation }).ToArray() };
        project = project with { Segments = project.Segments.Select(s => s.Id == id ? updated : s).ToArray() };
        return translation == old.Translation.Text ? project : ProjectEditor.EditTranslation(project, id, translation);
    }

    public static DubbingProject Merge(DubbingProject project, string firstId, string secondId)
    {
        var ordered = project.Segments.Where(s => !s.ExcludedFromDubbing).OrderBy(s => s.Start).ToArray();
        int index = Array.FindIndex(ordered, s => s.Id == firstId);
        if (index < 0 || index + 1 >= ordered.Length || ordered[index + 1].Id != secondId)
            throw new InvalidOperationException("Choose consecutive segments to merge.");
        var a = ordered[index]; var b = ordered[index + 1];
        var end = a.Start + ProjectAudioMix.EffectiveDuration(a);
        return SegmentCollisions.Resolve(project, new("", a, b, 0, 0, end > a.End ? end : a.End), CollisionChoice.Merge);
    }
}
