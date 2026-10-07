using System.Text.Json;
using Dubbing.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Integration.Tests;

public sealed class RuntimeFactAttribute : FactAttribute
{
    public RuntimeFactAttribute() { if (Environment.GetEnvironmentVariable("DUBBING_NATIVE_TESTS") != "1") Skip = "Opt in with DUBBING_NATIVE_TESTS=1 and runtime paths. No model downloads."; }
}
public sealed class NativeRuntimeTests
{
    [RuntimeFact] public async Task VlcRendersInitialVideoAndNewFrameAfterPausedSeek()
    {
        string video = Path.Combine(Path.GetTempPath(), "vlc-seek-" + Guid.NewGuid() + ".mp4");
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var result = await runner.RunAsync(new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG")!,
            ["-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", video]), default);
        Assert.Equal(0, result.ExitCode);
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(64 * 64 * 4);
        var frames = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            using var vlc = new LibVLCSharp.Shared.LibVLC("--no-audio", "--no-video-title-show");
            using var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
            player.SetVideoFormat("RV32", 64, 64, 256);
            player.SetVideoCallbacks((_, planes) => { System.Runtime.InteropServices.Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; }, null, (_, _) =>
            {
                var bytes = new byte[64 * 64 * 4]; System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, bytes.Length);
                frames.Enqueue(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            });
            using var media = new LibVLCSharp.Shared.Media(vlc, new Uri(video));
            bool firstPlay = true;
            player.Playing += (_, _) =>
            {
                if (!firstPlay) return;
                firstPlay = false;
                _ = Task.Run(() => { player.Time = 0; player.SetPause(true); player.NextFrame(); });
            };
            Assert.True(player.Play(media));
            await WaitUntil(() => frames.Count > 0);
            player.SetPause(true); await Task.Delay(150);
            string before = frames.Last(); frames.Clear();
            player.Time = 2500; player.NextFrame();
            await WaitUntil(() => frames.Any(f => f != before));
            Assert.False(player.IsPlaying);
            player.Play(); await WaitUntil(() => player.IsPlaying);
            player.Stop(); await Task.Delay(100);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); File.Delete(video); }
        static async Task WaitUntil(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
    }
    [RuntimeFact] public async Task BundledVlcParsesAudioWithoutExternalPlayer()
    {
        string audio = Path.Combine(Path.GetTempPath(), "vlc-test-" + Guid.NewGuid() + ".wav");
        try
        {
            await new Dubbing.Engines.Mock.MockTtsEngine().SynthesizeAsync(new() { Text = "test", Language = "en", OutputPath = audio, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
            LibVLCSharp.Shared.Core.Initialize();
            using var vlc = new LibVLCSharp.Shared.LibVLC("--no-audio", "--vout=dummy");
            using var media = new LibVLCSharp.Shared.Media(vlc, new Uri(audio));
            Assert.Equal(LibVLCSharp.Shared.MediaParsedStatus.Done, await media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseLocal, 5000).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.InRange(media.Duration, 990, 1010);
            // Parse can complete on VLC's callback thread. Release native objects from the pool.
            await Task.Delay(10);
        }
        finally { File.Delete(audio); }
    }
    [RuntimeFact] public async Task PinnedAudioServerStartsOnLoopbackListsModelsAndStops()
    {
        string root = Path.Combine(Path.GetTempPath(), "dubbing-runtime-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            string exe = Environment.GetEnvironmentVariable("DUBBING_AUDIO_SERVER")!;
            var version = await new ProcessRunner(NullLogger<ProcessRunner>.Instance).RunAsync(new(exe, ["--version"]), default);
            Assert.Contains("audio.cpp 0.9.0", version.StandardOutput);
            string config = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(config, """{"host":"127.0.0.1","backend":"cpu","lazy_load":true,"ui_management":true,"models":[]}""");
            using var host = new LocalServerHost();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Uri endpoint = await host.EnsureAsync(exe, "empty", port => ["--config", config, "--host", "127.0.0.1", "--port", port.ToString(), "--no-ui"], cancel.Token);
            Assert.Equal("127.0.0.1", endpoint.Host);
            using var http = new HttpClient();
            using var models = JsonDocument.Parse(await http.GetStringAsync(new Uri(endpoint, "v1/models"), cancel.Token));
            Assert.True(models.RootElement.TryGetProperty("data", out _));
            host.Stop();
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(new Uri(endpoint, "health")));
        }
        finally { Directory.Delete(root, true); }
    }
}
