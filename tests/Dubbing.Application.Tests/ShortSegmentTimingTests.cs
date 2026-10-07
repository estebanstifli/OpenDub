using Dubbing.Application.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Tests;

public sealed class ShortSegmentTimingTests
{
    private static DubSegment Segment(string id, double start, double end) => new()
    { Id = id, Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Source = new("en", "Yes"), Translation = new("es", "Sí", true), SpeakerId = "speaker" };
    [Fact] public void UsesEndThenStartWithoutMovingWordsOrTranslation()
    {
        var shortOne = Segment("a", 2, 2.5);
        var result = ShortSegmentTiming.Expand([shortOne, Segment("b", 2.8, 4)], TimeSpan.FromSeconds(5));
        Assert.Equal(1.7, result[0].Start.TotalSeconds, 6); Assert.Equal(2.8, result[0].End.TotalSeconds, 6);
        Assert.Equal(shortOne.Translation, result[0].Translation); Assert.Equal(shortOne.SpeakerId, result[0].SpeakerId);
        Assert.Same(shortOne.Words, result[0].Words);
        Assert.Equal(result, ShortSegmentTiming.Expand(result, TimeSpan.FromSeconds(5)));
    }
    [Fact] public void KeepsCrowdedAndOverlappingSegments()
    {
        var crowded = new[] { Segment("a", 0, 1), Segment("b", 1, 1.2), Segment("c", 1.2, 3) };
        Assert.Equal(crowded, ShortSegmentTiming.Expand(crowded, TimeSpan.FromSeconds(3)));
        var overlapping = new[] { Segment("a", 0, .5), Segment("b", .2, 2) };
        Assert.Equal(overlapping, ShortSegmentTiming.Expand(overlapping, TimeSpan.FromSeconds(3)));
    }
    [Fact] public void RespectsVideoBoundsAndOnlySelectedSegment()
    {
        var result = ShortSegmentTiming.Expand([Segment("a", 0, .4), Segment("b", 9.8, 10)], TimeSpan.FromSeconds(10), "b");
        Assert.Equal(.4, result[0].End.TotalSeconds);
        Assert.Equal(8.9, result[1].Start.TotalSeconds, 6); Assert.Equal(10, result[1].End.TotalSeconds);
    }
}
