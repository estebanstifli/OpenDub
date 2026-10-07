using Dubbing.Application.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Tests;

public class TranscriptQualityTests
{
    private static DubSegment Repetition => new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(8), Source = new("en", string.Join(" ", Enumerable.Repeat("Ha", 100))) };

    [Fact] public void ExcludesRunawayUnknownSpeechWithoutDeletingOriginal()
    {
        var original = Repetition;
        var result = Assert.Single(TranscriptQuality.ExcludeSuspectRepetitions([original]));
        Assert.True(result.ExcludedFromDubbing);
        Assert.NotNull(result.ExclusionReason);
        Assert.Equal(original.Id, result.Id);
        Assert.Equal(original.Source, result.Source);
        Assert.Equal(original.Start, result.Start);
        Assert.Equal(original.End, result.End);
        Assert.False(original.ExcludedFromDubbing);
    }

    [Fact] public void PreservesShortLaughterNormalUnknownDialogueAndHumanWork()
    {
        DubSegment[] keep = [
            Repetition with { Source = new("en", "Ha ha ha!") },
            Repetition with { Source = new("en", "I've seen things you people wouldn't believe.") },
            Repetition with { SpeakerId = "speaker1" },
            Repetition with { Translation = new("es", "", true) },
            Repetition with { Translation = new("es", "Ja ja") },
            Repetition with { GeneratedSpeech = new("speech.wav", TimeSpan.FromSeconds(8), "cache") },
            Repetition with { Source = Repetition.Source with { Locked = true } }
        ];
        Assert.All(TranscriptQuality.ExcludeSuspectRepetitions(keep), s => Assert.False(s.ExcludedFromDubbing));
    }

    [Fact] public void ReviewIsIdempotentAndPunctuationDoesNotHideRepetition()
    {
        Assert.True(TranscriptQuality.HasRunawayRepetition(string.Join(" ", Enumerable.Repeat("HA, ha!", 20))));
        var once = TranscriptQuality.ExcludeSuspectRepetitions([Repetition]);
        Assert.Equal(once, TranscriptQuality.ExcludeSuspectRepetitions(once));
    }
}
