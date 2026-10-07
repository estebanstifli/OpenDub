using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Tests;

public sealed class SegmentNormalizerTests
{
    private static DubSegment Segment(double start, double end, string text, string? speaker = "a") => new()
    { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), SpeakerId = speaker, Source = new("en", text), Translation = new("es", "old"), Words = [new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end))] };

    [Fact] public void WordsBecomeSentencesOrParagraphsWithOriginalWordTimes()
    {
        DubSegment[] source = [Segment(0, .24, "I"), Segment(.6, .8, "have"), Segment(1.2, 3.8, "seen things."), Segment(4.2, 5, "Today"), Segment(6, 8, "it rains.")];
        var normalizer = new SegmentNormalizer();
        var sentences = normalizer.Normalize(source);
        Assert.Equal(2, sentences.Count); Assert.Equal("I have seen things.", sentences[0].Source.Text);
        Assert.Equal(TimeSpan.Zero, sentences[0].Start); Assert.Equal(TimeSpan.FromSeconds(3.8), sentences[0].End);
        Assert.Equal(source.Take(3).SelectMany(s => s.Words), sentences[0].Words);
        Assert.Equal("", sentences[0].Translation.Text);
        Assert.Single(normalizer.Normalize(source, paragraphs: true));
        Assert.Equal(sentences, normalizer.Normalize(sentences));
    }
    [Fact] public void SpeakerChangesPausesOverlapsAndDurationLimitKeepBoundaries()
    {
        var n = new SegmentNormalizer();
        Assert.Equal(2, n.Normalize([Segment(0, 1, "one"), Segment(1.1, 2, "two", "b")]).Count);
        Assert.Equal(2, n.Normalize([Segment(0, 1, "one"), Segment(4, 5, "two")]).Count);
        Assert.Equal(2, n.Normalize([Segment(0, 2, "one"), Segment(1, 3, "two")]).Count);
        Assert.Equal(2, n.Normalize([Segment(0, 12, "one"), Segment(12, 20, "two")]).Count);
        Assert.Equal(2, n.Normalize([Segment(0, 1, "one", null), Segment(1, 2, "two", null)]).Count);
    }
    [Fact] public void ManualEditsExclusionsEmotionOverridesAndGeneratedAudioArePreserved()
    {
        var n = new SegmentNormalizer();
        var first = Segment(0, 1, "one");
        foreach (var second in new[] { Segment(1, 2, "two") with { Translation = new("es", "manual", true) },
            Segment(1, 2, "two") with { ExcludedFromDubbing = true },
            Segment(1, 2, "two") with { GeneratedSpeech = new("audio.wav", TimeSpan.FromSeconds(1), "key") },
            Segment(1, 2, "two") with { Emotion = new() { Mode = EmotionMode.Label, Label = "sad" } } })
        { var result = n.Normalize([first, second]); Assert.Equal(2, result.Count); Assert.Same(second, result[1]); }
    }
}
