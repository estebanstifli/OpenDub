using Dubbing.Application.Pipeline;

namespace Dubbing.Application.Tests;

public class ExportOptionsTests
{
    [Theory]
    [InlineData("00:10:05", 605)]
    [InlineData("00:10:05.250", 605.25)]
    [InlineData("25:00:00", 90000)]
    public void TimesSupportHoursMinutesSecondsAndFractions(string text, double seconds)
        => Assert.Equal(seconds, ExportOptions.ParseTime(text).TotalSeconds);

    [Theory]
    [InlineData("00:60:00")]
    [InlineData("00:00:60")]
    [InlineData("-01:00:00")]
    [InlineData("00:10")]
    [InlineData("")]
    public void InvalidTimesAreRejected(string text) => Assert.Throws<FormatException>(() => ExportOptions.ParseTime(text));

    [Fact]
    public void ExportOverlapReviewIgnoresCollisionsOutsideSelectedRange()
    {
        var project = new Dubbing.Core.Projects.DubbingProject { Segments = [
            new() { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(5) },
            new() { Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(6) }] };
        Assert.NotNull(Dubbing.Application.Projects.SegmentCollisions.Find(project));
        Assert.Null(Dubbing.Application.Projects.SegmentCollisions.Find(project, rangeStart: TimeSpan.Zero, rangeEnd: TimeSpan.FromSeconds(4)));
        Assert.Null(Dubbing.Application.Projects.SegmentCollisions.Find(project, rangeStart: TimeSpan.FromSeconds(5), rangeEnd: TimeSpan.FromSeconds(6)));
        Assert.NotNull(Dubbing.Application.Projects.SegmentCollisions.Find(project, rangeStart: TimeSpan.FromSeconds(4), rangeEnd: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DefaultIsEntireDurationAndRangesMustBeInsideSource()
    {
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(10)), new ExportOptions().Range(TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new ExportOptions { End = TimeSpan.FromMinutes(11) }.Range(TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentException>(() => new ExportOptions { Start = TimeSpan.FromSeconds(-1) }.Range(TimeSpan.FromMinutes(10)));
    }
}
