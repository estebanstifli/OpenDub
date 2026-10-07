using Dubbing.Core.Projects;
using Dubbing.Core.Pipeline;

namespace Dubbing.Application.Pipeline;

public static class ShortSegmentTiming
{
    // Leave a small margin over the native one-second reference limit.
    public static IReadOnlyList<DubSegment> Expand(IReadOnlyList<DubSegment> input, TimeSpan duration, string? onlyId = null)
    {
        var result = input.ToArray();
        foreach (int i in Enumerable.Range(0, result.Length).OrderBy(i => result[i].Start))
        {
            var s = result[i];
            if (s.ExcludedFromDubbing || string.IsNullOrWhiteSpace(s.Source.Text) || s.TargetDuration >= TimeSpan.FromSeconds(1) ||
                s.TargetDuration <= TimeSpan.Zero || s.GeneratedSpeech is not null || (onlyId is not null && s.Id != onlyId)) continue;
            // Existing overlaps cannot be safely enlarged.
            if (result.Any(n => n.Id != s.Id && n.Start < s.End && n.End > s.Start)) continue;
            var lower = result.Where(n => n.Id != s.Id && n.End <= s.Start).Select(n => n.End).DefaultIfEmpty(TimeSpan.Zero).Max();
            var upper = result.Where(n => n.Id != s.Id && n.Start >= s.End).Select(n => n.Start).DefaultIfEmpty(duration).Min();
            upper = upper < duration ? upper : duration;
            var end = s.Start + TimeSpan.FromSeconds(1.1);
            if (end > upper) end = upper;
            var start = s.Start;
            if (end-start < TimeSpan.FromSeconds(1.1)) start = end-TimeSpan.FromSeconds(1.1);
            if (start < lower) start = lower;
            // Keep the original range if the available gap cannot meet the minimum.
            if (end-start < TimeSpan.FromSeconds(1) || start > s.Start || end < s.End) continue;
            result[i] = s with { Start = start, End = end, FittedSpeech = null,
                DirtyStages = s.DirtyStages.Union(new[] { PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux }).ToArray() };
        }
        return result;
    }
}
