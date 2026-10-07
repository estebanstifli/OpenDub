using Dubbing.Application.Playback;
using LibVLCSharp.Shared;

namespace Dubbing.App.Services;

// Owns only the dialog's audition audio; never mutes the shared device or opens video.
internal sealed class OverlapAudioPlayer : IDisposable
{
    private LibVLC? vlc;
    private MediaPlayer? player;
    private TimelineAudioStream? stream;
    private StreamMediaInput? input;
    private LibVLCSharp.Shared.Media? media;
    public double Position => Math.Max(0, (player?.Time ?? 0) / 1000d);
    public bool IsPlaying => player?.IsPlaying == true;
    public bool IsPaused => player?.State == VLCState.Paused;
    public async Task PlayAsync(RealtimePlaybackPlan plan, double start, double end, CancellationToken ct)
    {
        Stop();
        if (vlc is null) { LibVLCSharp.Shared.Core.Initialize(); vlc = new LibVLC("--no-video", "--no-video-title-show"); player = new MediaPlayer(vlc); }
        stream = new TimelineAudioStream(plan.Duration); stream.Update(plan.Clips);
        input = new StreamMediaInput(stream); media = new LibVLCSharp.Shared.Media(vlc, input);
        media.AddOption(":demux=wav"); media.AddOption(":file-caching=50"); media.AddOption(":no-video");
        media.AddOption(":start-time=" + start.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        media.AddOption(":stop-time=" + end.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        player!.Mute = false; player.Volume = 100;
        if (!player.Play(media)) throw new InvalidOperationException("Audio preview could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!player.IsPlaying)
        {
            if (player.State == VLCState.Error) throw new InvalidOperationException("Audio preview could not start.");
            await Task.Delay(25, timeout.Token);
        }
        player.Time = (long)(start * 1000);
    }
    public void Seek(double seconds) { if (player is not null) player.Time = (long)(seconds * 1000); }
    public void Pause() => player?.SetPause(true);
    public void Resume() => player?.SetPause(false);
    public void Stop()
    {
        player?.Stop(); if (player is not null) player.Media = null;
        media?.Dispose(); media = null; input?.Dispose(); input = null; stream?.Dispose(); stream = null;
    }
    public void Dispose() { Stop(); player?.Dispose(); vlc?.Dispose(); }
}
