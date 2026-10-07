using System.Text.RegularExpressions;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

// A conservative review guard, not a speech detector. Keep the original text and timing.
public static class TranscriptQuality
{
    public static bool HasRunawayRepetition(string text)
    {
        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value).ToArray();
        if (tokens.Length < 20) return false;
        return tokens.GroupBy(t => t).Max(g => g.Count()) >= tokens.Length * .8;
    }

    public static IReadOnlyList<DubSegment> ExcludeSuspectRepetitions(IReadOnlyList<DubSegment> segments) => segments.Select(s =>
        !s.ExcludedFromDubbing && s.SpeakerId is null && !s.Source.Locked && !s.Translation.Locked &&
        string.IsNullOrWhiteSpace(s.Translation.Text) && s.GeneratedSpeech is null && s.FittedSpeech is null && HasRunawayRepetition(s.Source.Text)
            ? s with { ExcludedFromDubbing = true, ExclusionReason = "Suspected ASR repetition · review original audio" }
            : s).ToArray();
}
