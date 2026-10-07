using Dubbing.Application.Media;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Projects;

public enum CollisionChoice { Cancel, Limit, Merge, Allow }
public sealed record SegmentCollision(string Key, DubSegment First, DubSegment Second, int FirstNumber, int SecondNumber, TimeSpan AudibleEnd);
public sealed record CollisionDecision(CollisionChoice Choice, bool ApplyToAll = false);

public static class SegmentCollisions
{
    // Shared boundaries are valid. Ignore only sub-frame/sample rounding (up to 2 ms).
    public static readonly TimeSpan BoundaryTolerance = TimeSpan.FromMilliseconds(2);
    public static SegmentCollision? Find(DubbingProject project, IReadOnlySet<string>? affectedIds = null)
    {
        var active = project.Segments.Select((s, i) => (Segment: s, Number: i + 1)).Where(x => !x.Segment.ExcludedFromDubbing).OrderBy(x => x.Segment.Start).ToArray();
        var affected = affectedIds is null ? null : Enumerable.Range(0, active.Length).Where(i => affectedIds.Contains(active[i].Segment.Id)).ToArray();
        if (affected is { Length: 0 }) return null;
        for (int i = 0; i < active.Length; i++)
        {
            var a = active[i]; var speech = ProjectAudioMix.EffectiveSpeech(a.Segment);
            TimeSpan end = a.Segment.ClipAudioToSlot ? a.Segment.End : new(Math.Max(a.Segment.End.Ticks, a.Segment.Start.Ticks + (speech?.Duration.Ticks ?? 0)));
            // For unchanged segments, compare only against changed ones. Include earlier
            // long clips too: the overlapping neighbour need not be immediately adjacent.
            IEnumerable<int> candidates = affectedIds is null || affectedIds.Contains(a.Segment.Id)
                ? Enumerable.Range(i + 1, active.Length - i - 1) : affected!;
            foreach (int j in candidates)
            {
                if (j <= i) continue;
                var b = active[j];
                if (b.Segment.Start >= end - BoundaryTolerance) break;
                string key = StageCache.Key(a.Segment.Id, a.Segment.Start, a.Segment.End, speech?.Fingerprint, speech?.Duration, a.Segment.ClipAudioToSlot,
                    b.Segment.Id, b.Segment.Start, b.Segment.End, ProjectAudioMix.EffectiveSpeech(b.Segment)?.Fingerprint);
                if (!project.AcceptedOverlaps.Contains(key)) return new(key, a.Segment, b.Segment, a.Number, b.Number, end);
            }
        }
        return null;
    }
    public static DubbingProject Resolve(DubbingProject project, SegmentCollision collision, CollisionChoice choice)
    {
        var a = collision.First; var b = collision.Second;
        if (choice == CollisionChoice.Allow) return project with { AcceptedOverlaps = project.AcceptedOverlaps.Append(collision.Key).TakeLast(4096).ToArray() };
        if (choice == CollisionChoice.Limit)
        {
            if (b.Start <= a.Start) throw new InvalidOperationException("Segments with the same start cannot be limited. Merge them or keep the overlap.");
            var limited = a with { End = a.End < b.Start ? a.End : b.Start, ClipAudioToSlot = true,
                SourceReferenceStart = a.SourceReferenceStart ?? a.Start, SourceReferenceEnd = a.SourceReferenceEnd ?? a.End,
                DirtyStages = a.DirtyStages.Union([PipelineStage.Mix, PipelineStage.Mux]).ToArray() };
            return project with { Segments = project.Segments.Select(s => s.Id == a.Id ? limited : s).ToArray() };
        }
        if (choice != CollisionChoice.Merge) return project;
        TimeSpan end = new(Math.Max(Math.Max(a.End.Ticks, b.End.Ticks), collision.AudibleEnd.Ticks));
        if (project.Source is { } source && end > source.Duration) end = source.Duration;
        bool translate = string.IsNullOrWhiteSpace(a.Translation.Text) || string.IsNullOrWhiteSpace(b.Translation.Text) ||
            a.Translation.Language != project.Settings.TargetLanguage || b.Translation.Language != project.Settings.TargetLanguage ||
            a.DirtyStages.Contains(PipelineStage.Translation) || b.DirtyStages.Contains(PipelineStage.Translation);
        var merged = a with { End = end, ClipAudioToSlot = false,
            SourceReferenceStart = new(Math.Min((a.SourceReferenceStart ?? a.Start).Ticks, (b.SourceReferenceStart ?? b.Start).Ticks)),
            SourceReferenceEnd = new(Math.Max((a.SourceReferenceEnd ?? a.End).Ticks, (b.SourceReferenceEnd ?? b.End).Ticks)),
            Source = a.Source with { Text = Join(a.Source.Text, b.Source.Text) },
            Translation = new(project.Settings.TargetLanguage, Join(a.Translation.Text, b.Translation.Text), a.Translation.Locked || b.Translation.Locked),
            Words = a.Words.Concat(b.Words).OrderBy(w => w.Start).ToArray(), GeneratedSpeech = null, FittedSpeech = null,
            Status = SegmentStatus.TtsDirty,
            DirtyStages = (translate ? new[] { PipelineStage.Translation, PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux }
                : new[] { PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux }) };
        return project with { Segments = project.Segments.Where(s => s.Id != b.Id).Select(s => s.Id == a.Id ? merged : s).ToArray() };
    }
    private static string Join(string a, string b) => string.Join(" ", new[] { a.Trim(), b.Trim() }.Where(x => x.Length > 0));
}
