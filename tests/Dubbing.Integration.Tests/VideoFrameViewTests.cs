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
    [ProjectPlaybackFact] public async Task ExistingProjectPlaysDubAndBackgroundThroughEnabledAudioOutput()
    {
        string directory = Environment.GetEnvironmentVariable("OPENDUB_PLAYBACK_PROJECT")!;
        var store = new Dubbing.Infrastructure.Projects.JsonProjectStore(new Dubbing.Infrastructure.Projects.ProjectJson(), NullLogger<Dubbing.Infrastructure.Projects.JsonProjectStore>.Instance);
        var project = await store.LoadAsync(directory, default);
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var media = new Dubbing.Media.FFmpeg.FFmpegMediaEngine(runner, new RuntimeLocator(), new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG"), Environment.GetEnvironmentVariable("DUBBING_FFPROBE")));
        var service = new Dubbing.Application.Playback.RealtimeAudioService(media);
        var dub = await service.PrepareAsync(project, directory, null, default, Dubbing.Application.Pipeline.AudioAudition.Dub);
        var background = await service.PrepareAsync(project, directory, null, default, Dubbing.Application.Pipeline.AudioAudition.Background);
        Assert.NotEmpty(dub.Clips); Assert.NotEmpty(background.Clips);
        var position = dub.Clips.First().Start + TimeSpan.FromSeconds(.5);
        foreach (var plan in new[] { dub, background })
        {
            using var pcm = new Dubbing.Application.Playback.TimelineAudioStream(plan.Duration);
            pcm.Update(plan.Clips); pcm.Position = 80 + (long)(position.TotalSeconds * 48000) * 4;
            var samples = new byte[9600]; pcm.ReadExactly(samples);
            Assert.Contains(samples, b => b != 0);
        }
        var session = Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch(async () =>
        {
            var window = new Dubbing.App.MainWindow(); window.Show();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var play = typeof(Dubbing.App.MainWindow).GetMethod("PlayRealtimeAsync", flags)!;
            try
            {
                foreach (var plan in new[] { background, dub })
                {
                    await (Task)play.Invoke(window, [plan, position])!;
                    var audio = (MediaPlayer)typeof(Dubbing.App.MainWindow).GetField("realtimePlayer", flags)!.GetValue(window)!;
                    var video = (MediaPlayer)typeof(Dubbing.App.MainWindow).GetField("player", flags)!.GetValue(window)!;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (audio.Time < position.TotalMilliseconds + 300) await Task.Delay(30, timeout.Token);
                    Assert.False(audio.Mute); Assert.Equal(100, audio.Volume); Assert.True(audio.AudioTrack >= 0);
                    Assert.True(video.AudioTrack < 0);
                }
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    [RuntimeFact] public async Task RealtimePlayerKeepsVideoMutedAndSupportsSeekPauseResume()
    {
        var session = Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        await session.Dispatch(async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "realtime-video-" + Guid.NewGuid()); Directory.CreateDirectory(root);
            string video = Path.Combine(root, "source.mp4"), voice = Path.Combine(root, "voice.wav");
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            var result = await runner.RunAsync(new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG")!,
                ["-y", "-f", "lavfi", "-i", "testsrc2=s=128x72:r=20:d=12", "-f", "lavfi", "-i", "sine=frequency=600:duration=12", "-c:v", "libx264", "-c:a", "aac", "-shortest", video]), default);
            Assert.Equal(0, result.ExitCode);
            await new Dubbing.Engines.Mock.MockTtsEngine().SynthesizeAsync(new() { Text = "test", Language = "en", OutputPath = voice, TargetDuration = TimeSpan.FromSeconds(12) }, null, default);
            var plan = new Dubbing.Application.Playback.RealtimePlaybackPlan(video, TimeSpan.FromSeconds(12), [new(voice, TimeSpan.Zero, Limit: TimeSpan.FromSeconds(12))]);
            var window = new Dubbing.App.MainWindow(); window.Show();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object? Field(string name) => typeof(Dubbing.App.MainWindow).GetField(name, flags)!.GetValue(window);
            object? Call(string name, params object[] args) => typeof(Dubbing.App.MainWindow).GetMethod(name, flags)!.Invoke(window, args);
            try
            {
                await (Task)Call("PlayRealtimeAsync", plan, TimeSpan.FromSeconds(1))!;
                var audio = Assert.IsType<MediaPlayer>(Field("realtimePlayer"));
                var player = Assert.IsType<MediaPlayer>(Field("player"));
                await WaitFor(() => audio.Time > 1500 && player.IsPlaying);
                Assert.False(audio.Mute);
                Assert.True(audio.Volume > 0);
                Assert.True(audio.AudioTrack >= 0);
                Assert.True(player.AudioTrack < 0);
                Assert.NotNull(window.FindControl<VideoFrameView>("VideoSurface")!.Source);
                Call("Pause"); await WaitFor(() => audio.State == VLCState.Paused && player.State == VLCState.Paused);
                Call("Seek", 6d); await WaitFor(() => audio.Time >= 5900);
                await (Task)Call("PlayRealtimeAsync", plan, TimeSpan.FromSeconds(6))!;
                await WaitFor(() => audio.Time > 6500 && player.Time > 6200);
                Call("SynchronizeVideo"); await Task.Delay(250);
                Assert.True(Math.Abs(audio.Time - player.Time) < 600, $"audio={audio.Time}, video={player.Time}");
                Assert.True(player.AudioTrack < 0); Assert.False(audio.Mute); Assert.Equal(100, audio.Volume);
                await (Task)Call("UpdateRealtimeAsync", plan with { Clips = [] })!;
                Assert.True(audio.IsPlaying);
                Call("Seek", 11.6d);
                await WaitFor(() => audio.State == VLCState.Ended);
                await (Task)Call("PlayRealtimeAsync", plan, TimeSpan.FromSeconds(1))!;
                await WaitFor(() => audio.IsPlaying && audio.Time > 1400 && audio.Time < 5000);
                Assert.True(player.AudioTrack < 0); Assert.False(audio.Mute);
                Call("Stop"); Assert.Null(Field("realtimePlayer"));
                await (Task)Call("PlayMediaAsync", video, TimeSpan.FromSeconds(1))!;
                await WaitFor(() => player.IsPlaying && player.AudioTrack >= 0);
                Assert.False(player.Mute);
            }
            finally { window.Close(); Directory.Delete(root, true); }
            return true;
        }, default);
        static async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
    }
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

public sealed class ProjectPlaybackFactAttribute : FactAttribute
{
    public ProjectPlaybackFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DUBBING_NATIVE_TESTS") != "1" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENDUB_PLAYBACK_PROJECT")))
            Skip = "Requires native VLC and an explicit OPENDUB_PLAYBACK_PROJECT (read-only).";
    }
}
