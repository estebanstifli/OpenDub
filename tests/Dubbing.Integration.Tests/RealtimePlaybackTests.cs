using System.Buffers.Binary;
using Dubbing.Application.Playback;
using Dubbing.Application.Projects;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using LibVLCSharp.Shared;

namespace Dubbing.Integration.Tests;

public sealed class RealtimePlaybackTests : IDisposable
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void SharedBoundaryIsValidAndOnlyExcessBeyondRoundingToleranceOverlaps(int excessMs, bool overlaps)
    {
        var a = new DubSegment { Start = TimeSpan.FromSeconds(15), End = TimeSpan.FromMilliseconds(17400 + excessMs) };
        var b = new DubSegment { Start = TimeSpan.FromMilliseconds(17400), End = TimeSpan.FromSeconds(20) };
        var project = new DubbingProject { Segments = [a, b] };
        Assert.Equal(overlaps, SegmentCollisions.Find(project) is not null);
        Assert.Equal(overlaps, SegmentCollisions.Find(project, new HashSet<string> { b.Id }) is not null);
        // The same boundary rule applies to measured audio, not only the segment box.
        a = a with { End = b.Start, GeneratedSpeech = new("a.wav", TimeSpan.FromMilliseconds(2400 + excessMs), "a", "a"), DirtyStages = [] };
        Assert.Equal(overlaps, SegmentCollisions.Find(project with { Segments = [a, b] }) is not null);
    }
    [Fact]
    public void ScopedReviewFindsEarlierLongClipsAndLaterNeighboursButIgnoresUnrelatedPairs()
    {
        DubSegment Segment(double start, double end) => new() { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) };
        var longClip = Segment(0, 10); var middle = Segment(2, 3); var edited = Segment(8, 12); var next = Segment(11, 13);
        var project = new DubbingProject { Segments = [longClip, middle, edited, next, Segment(20, 24), Segment(22, 26)] };
        var selection = new HashSet<string> { edited.Id };
        var collision = SegmentCollisions.Find(project, selection)!;
        Assert.Equal(longClip.Id, collision.First.Id); Assert.Equal(edited.Id, collision.Second.Id);
        project = SegmentCollisions.Resolve(project, collision, CollisionChoice.Allow);
        collision = SegmentCollisions.Find(project, selection)!;
        Assert.Equal(edited.Id, collision.First.Id); Assert.Equal(next.Id, collision.Second.Id);
        project = SegmentCollisions.Resolve(project, collision, CollisionChoice.Allow);
        Assert.Null(SegmentCollisions.Find(project, selection));
        Assert.NotNull(SegmentCollisions.Find(project));
        Assert.Null(SegmentCollisions.Find(project, new HashSet<string>()));
    }
    [Fact]
    public void TextEditsKeepFinishedFittedAudioUntilRegeneration()
    {
        var a = new DubSegment { End = TimeSpan.FromSeconds(2),
            GeneratedSpeech = new("raw.wav", TimeSpan.FromSeconds(3), "raw", "raw"),
            FittedSpeech = new("fit.wav", TimeSpan.FromSeconds(2), "fit", "fit"), DirtyStages = [] };
        var project = new DubbingProject { Segments = [a, new() { Start = a.End, End = TimeSpan.FromSeconds(4) }] };
        project = ProjectEditor.EditTranslation(project, a.Id, "New translation");
        Assert.Equal(a.FittedSpeech, Dubbing.Application.Pipeline.ProjectAudioMix.EffectiveSpeech(project.Segments[0]));
        Assert.Null(SegmentCollisions.Find(project));
        // Once a new raw synthesis has completed it replaces the old fitted audio.
        var regenerated = a with { FittedSpeech = null, DirtyStages = [Dubbing.Core.Pipeline.PipelineStage.DurationFit] };
        Assert.NotNull(SegmentCollisions.Find(project with { Segments = [regenerated, project.Segments[1]] }));
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "realtime-" + Guid.NewGuid());
    private async Task<string> Wave(string name, int frequency)
    {
        Directory.CreateDirectory(root); string path = Path.Combine(root, name + ".wav");
        await new MockTtsEngine(frequency: frequency).SynthesizeAsync(new() { Text = name, Language = "es", OutputPath = path, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
        return path;
    }
    private static byte[] ReadAt(TimelineAudioStream stream, double seconds, int frames = 2400)
    {
        stream.Position = 80 + (long)(seconds * 48000) * 4;
        var bytes = new byte[frames * 4]; int read = 0;
        while (read < bytes.Length) { int n = stream.Read(bytes, read, bytes.Length - read); if (n == 0) break; read += n; }
        return bytes;
    }
    [Fact]
    public async Task ReplacingOneClipKeepsOtherClipsAndReadsOnlyNearbyBlocks()
    {
        string a = await Wave("a", 440), b = await Wave("b", 660), c = await Wave("c", 880), replacement = await Wave("replacement", 1100);
        using var stream = new TimelineAudioStream(TimeSpan.FromHours(4));
        AudioClip Clip(string path, double time) => new(path, TimeSpan.FromSeconds(time), Limit: TimeSpan.FromSeconds(1));
        var clips = new[] { Clip(a, 1), Clip(b, 3), Clip(c, 5) }; stream.Update(clips);
        Assert.Equal(0, stream.OpenSources);
        Assert.All(ReadAt(stream, .2), x => Assert.Equal(0, x));
        var first = ReadAt(stream, 1.2); var second = ReadAt(stream, 3.2); var third = ReadAt(stream, 5.2);
        Assert.Contains(first, b => b != 0);
        Assert.True(stream.SourceBytesRead < 150000);
        stream.Update([clips[0], Clip(replacement, 3), clips[2]]);
        Assert.Equal(first, ReadAt(stream, 1.2)); Assert.NotEqual(second, ReadAt(stream, 3.2)); Assert.Equal(third, ReadAt(stream, 5.2));
        stream.Update([clips[0], Clip(replacement, 3) with { Limit = TimeSpan.FromSeconds(.3) }, clips[2]]);
        Assert.All(ReadAt(stream, 3.5), x => Assert.Equal(0, x)); Assert.Equal(third, ReadAt(stream, 5.2));
        // Moving a clip reuses its PCM; no files were synthesized or rendered by the mixer.
        stream.Update([clips[0], Clip(replacement, 7), clips[2]]);
        Assert.All(ReadAt(stream, 3.2), x => Assert.Equal(0, x)); Assert.Contains(ReadAt(stream, 7.2), x => x != 0);
        Assert.Equal(4, Directory.GetFiles(root).Length);
    }
    [Fact]
    public void CollisionsCanBeLimitedMergedOrAcknowledgedWithoutMovingFollowingSegments()
    {
        var a = new DubSegment { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3), SpeakerId = "a", Source = new("en", "Hello."), Translation = new("es", "Hola."), GeneratedSpeech = new("a.wav", TimeSpan.FromSeconds(3), "a", "fp-a"), DirtyStages = [] };
        var b = new DubSegment { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(5), SpeakerId = "b", Source = new("en", "World."), Translation = new("es", "Mundo."), DirtyStages = [] };
        var c = new DubSegment { Start = TimeSpan.FromSeconds(6), End = TimeSpan.FromSeconds(7) };
        var project = new DubbingProject { Segments = [a,b,c] }; var collision = SegmentCollisions.Find(project)!;
        Assert.Equal(1, collision.FirstNumber); Assert.Equal(2, collision.SecondNumber);
        var limited = SegmentCollisions.Resolve(project, collision, CollisionChoice.Limit);
        Assert.True(limited.Segments[0].ClipAudioToSlot); Assert.Null(SegmentCollisions.Find(limited)); Assert.Equal(c, limited.Segments[2]);
        var merged = SegmentCollisions.Resolve(project, collision, CollisionChoice.Merge);
        Assert.Equal(2, merged.Segments.Count); Assert.Equal("Hola. Mundo.", merged.Segments[0].Translation.Text);
        Assert.Equal("a", merged.Segments[0].SpeakerId); Assert.Null(merged.Segments[0].GeneratedSpeech); Assert.Equal(c, merged.Segments[1]);
        var allowed = SegmentCollisions.Resolve(project, collision, CollisionChoice.Allow); Assert.Null(SegmentCollisions.Find(allowed));
        allowed = allowed with { Segments = [a with { GeneratedSpeech = a.GeneratedSpeech with { Fingerprint = "new", Duration = TimeSpan.FromSeconds(4) } },b,c] };
        Assert.NotNull(SegmentCollisions.Find(allowed));
    }
    [RuntimeFact]
    public async Task VirtualWaveDecodesInVlcAndSupportsSeekAndLiveMute()
    {
        string voice = await Wave("voice", 440);
        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--no-video-title-show");
        using var stream = new TimelineAudioStream(TimeSpan.FromSeconds(12));
        stream.Update(Enumerable.Range(0, 12).Select(i => new AudioClip(voice, TimeSpan.FromSeconds(i), Limit: TimeSpan.FromSeconds(1))).ToArray());
        using var input = new StreamMediaInput(stream);
        using var player = new MediaPlayer(vlc);
        long audible = 0, silent = 0;
        player.SetAudioFormat("S16N", 48000, 2);
        player.SetAudioCallbacks((_, ptr, count, _) =>
        {
            var values = new short[count * 2]; System.Runtime.InteropServices.Marshal.Copy(ptr, values, 0, values.Length);
            if (values.Any(s => Math.Abs((int)s) > 100)) Interlocked.Increment(ref audible); else Interlocked.Increment(ref silent);
        }, null, null, null, null);
        using var media = new LibVLCSharp.Shared.Media(vlc, input); media.AddOption(":demux=wav"); media.AddOption(":file-caching=50");
        Assert.True(player.Play(media));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (Interlocked.Read(ref audible) == 0) await Task.Delay(20, timeout.Token);
        player.Time = 5000;
        while (player.Time < 4500) await Task.Delay(20, timeout.Token);
        stream.Update([]); player.Time = 6000;
        while (Interlocked.Read(ref silent) == 0) await Task.Delay(20, timeout.Token);
        player.Stop(); Assert.True(stream.SourceBytesRead < 1000000);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
