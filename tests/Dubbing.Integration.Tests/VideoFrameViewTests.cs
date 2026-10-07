using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Dubbing.App.Controls;
using Dubbing.Infrastructure.Processes;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Integration.Tests;

public sealed class VideoFrameViewTests
{
    [RuntimeFact] public async Task StudioPlayerDecodesAudibleAudioAfterMediaSwitch()
    {
        var session = Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch(async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "studio-audio-" + Guid.NewGuid()); Directory.CreateDirectory(root);
            string source = Path.Combine(root, "source.mkv"), next = Path.Combine(root, "mix.mkv");
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var result = await runner.RunAsync(new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG")!,
                ["-y", "-f", "lavfi", "-i", "color=blue:s=64x64:r=10:d=4", "-f", "lavfi", "-i", "sine=frequency=600:duration=4", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", source]), default);
            Assert.Equal(0, result.ExitCode); File.Copy(source, next);
            var window = new Dubbing.App.MainWindow(); window.Show();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var play = typeof(Dubbing.App.MainWindow).GetMethod("Play", flags)!;
            try
            {
                play.Invoke(window, [source, TimeSpan.Zero]);
                var player = (MediaPlayer)typeof(Dubbing.App.MainWindow).GetField("player", flags)!.GetValue(window)!;
                long audible = 0;
                player.SetAudioFormat("S16N", 44100, 1);
                player.SetAudioCallbacks((_, samples, count, _) =>
                {
                    var pcm = new short[count]; System.Runtime.InteropServices.Marshal.Copy(samples, pcm, 0, pcm.Length);
                    if (pcm.Any(v => Math.Abs((int)v) > 100)) Interlocked.Increment(ref audible);
                }, null, null, null, null);
                await Wait();
                var primary = window.FindControl<VideoFrameView>("VideoSurface")!;
                var mirror = window.FindControl<Image>("SegmentVideo")!;
                using (var frameTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    while (mirror.Source is null) await Task.Delay(25, frameTimeout.Token);
                Assert.Same(primary.Source, mirror.Source);
                player.SetPause(true); player.Mute = true; player.Volume = 0;
                Interlocked.Exchange(ref audible, 0);
                play.Invoke(window, [next, TimeSpan.FromSeconds(1)]);
                await Wait();
                // Custom PCM callbacks bypass the device volume implementation.
                Assert.False(player.Mute); Assert.True(player.AudioTrack >= 0);
                async Task Wait()
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    while (Interlocked.Read(ref audible) == 0 || player.Mute || player.AudioTrack < 0) await Task.Delay(30, timeout.Token);
                }
            }
            finally { window.Close(); Directory.Delete(root, true); }
            return true;
        }, default);
    }
    [RuntimeFact] public async Task FramesRenderWithoutNativeWindowAcrossDetachPausedSeekAndReplay()
    {
        var session = Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch(async () => { await CheckFramesAsync(); return true; }, default);
    }
    private static async Task CheckFramesAsync()
    {
        string video = Path.Combine(Path.GetTempPath(), "frame-view-" + Guid.NewGuid() + ".mp4");
        var result = await new ProcessRunner(NullLogger<ProcessRunner>.Instance).RunAsync(new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG")!,
            ["-y", "-f", "lavfi", "-i", "testsrc2=size=128x72:rate=10:duration=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", video]), default);
        Assert.Equal(0, result.ExitCode);
        LibVLCSharp.Shared.Core.Initialize();
        using var vlc = new LibVLC("--no-audio", "--vout=vmem", "--avcodec-hw=none");
        using var surface = new VideoFrameView();
        using var player = new MediaPlayer(vlc);
        surface.Attach(player);
        var formats = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Record(string line) => formats.Enqueue(line);
        ActivityJournal.Entry += Record;
        var window = new Window { Width = 400, Height = 250, Content = surface };
        try
        {
            window.Show();
            using var media = new LibVLCSharp.Shared.Media(vlc, new Uri(video));
            await media.Parse(MediaParseOptions.ParseLocal, 5000);
            var track = media.Tracks.First(t => t.TrackType == TrackType.Video).Data.Video;
            surface.ConfigureSource(track.Width, track.Height);
            Assert.True(player.Play(media));
            await WaitFor(() => surface.PresentedFrames > 0);
            Assert.Equal(IntPtr.Zero, player.Hwnd);
            player.SetPause(true); await Task.Delay(150);
            var initial = Snapshot(); long count = surface.PresentedFrames;
            window.Content = null;
            player.Time = 2500; player.NextFrame();
            await WaitFor(() => surface.PresentedFrames > count);
            Assert.False(initial.SequenceEqual(Snapshot()));
            window.Content = surface; window.UpdateLayout();
            Assert.True(((WriteableBitmap)surface.Source!).PixelSize == new Avalonia.PixelSize(128, 72), string.Join("; ", formats));
            player.Stop(); count = surface.PresentedFrames;
            using var replay = new LibVLCSharp.Shared.Media(vlc, new Uri(video));
            Assert.True(player.Play(replay));
            await WaitFor(() => surface.PresentedFrames > count);
            Assert.Equal(IntPtr.Zero, player.Hwnd);
        }
        finally { ActivityJournal.Entry -= Record; player.Stop(); window.Content = null; window.Close(); File.Delete(video); }
        byte[] Snapshot() { using var stream = new MemoryStream(); ((WriteableBitmap)surface.Source!).Save(stream); return stream.ToArray(); }
        static async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
    }
}
