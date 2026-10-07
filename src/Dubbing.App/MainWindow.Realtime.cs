using System.Diagnostics;
using Avalonia.Threading;
using Dubbing.Application.Playback;
using Dubbing.Infrastructure.Processes;
using LibVLCSharp.Shared;

namespace Dubbing.App;

public partial class MainWindow
{
    private MediaPlayer? realtimePlayer;
    private StreamMediaInput? realtimeInput;
    private TimelineAudioStream? realtimeStream;
    private string? realtimeVideo;
    private TimeSpan realtimeDuration;
    private bool realtimeMode;
    private long realtimePendingTime, lastCorrection;
    private int realtimeVersion;

    private async Task PlayRealtimeAsync(RealtimePlaybackPlan plan, TimeSpan position)
    {
        int version = ++realtimeVersion;
        var watch = Stopwatch.StartNew();
        try
        {
            if (realtimeVideo != plan.Video || realtimeDuration != plan.Duration) ExitRealtime(false);
            realtimeMode = true; realtimeVideo = plan.Video; realtimeDuration = plan.Duration;
            realtimePendingTime = (long)position.TotalMilliseconds;
            realtimeStream ??= new TimelineAudioStream(plan.Duration);
            realtimeStream.Update(plan.Clips);
            await PlayMediaAsync(plan.Video, position);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (player is not null && player.State is not (VLCState.Playing or VLCState.Paused))
            { await Task.Delay(15, timeout.Token); if (version != realtimeVersion) return; }
            if (version != realtimeVersion || player is null || vlc is null) return;
            player.Mute = true; player.SetPause(true); player.Time = realtimePendingTime;
            pauseOnStart = false;
            if (realtimePlayer is null)
            {
                realtimeInput = new StreamMediaInput(realtimeStream);
                realtimePlayer = new MediaPlayer(vlc);
                var created = realtimePlayer;
                realtimePlayer.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!realtimeMode || !ReferenceEquals(realtimePlayer, created) || player is null) return;
                    realtimePlayer.Time = realtimePendingTime;
                    player.Time = realtimePendingTime; pauseOnStart = false; player.SetPause(false); player.Mute = true;
                    if (attached is not null) attached.IsPlaybackRunning = true;
                });
                realtimePlayer.EndReached += (_, _) => Dispatcher.UIThread.Post(() => { if (!ReferenceEquals(realtimePlayer, created)) return; player?.SetPause(true); if (attached is not null) attached.IsPlaybackRunning = false; });
                realtimePlayer.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(realtimePlayer, created)) return;
                    Pause(); if (attached is not null) attached.Status = attached.L["Realtime audio playback failed. Check the active WAV files."];
                });
                using var media = new LibVLCSharp.Shared.Media(vlc, realtimeInput);
                media.AddOption(":demux=wav"); media.AddOption(":file-caching=50"); media.AddOption(":network-caching=50");
                realtimePlayer.Play(media);
            }
            else
            {
                if (realtimePlayer.State is VLCState.Ended or VLCState.Error) realtimePlayer.Stop();
                realtimePlayer.Time = realtimePendingTime; realtimePlayer.Play();
            }
            ActivityJournal.Write($"Realtime playback · {plan.Clips.Count} clips · prepared in {watch.Elapsed.TotalMilliseconds:F0} ms · PCM blocks, no film render.");
        }
        catch (Exception ex)
        {
            if (version != realtimeVersion) return;
            Stop(); if (attached is not null) attached.Status = attached.L.Format("Playback unavailable: {0}", ex.Message);
        }
    }
    private Task UpdateRealtimeAsync(RealtimePlaybackPlan plan)
    {
        if (!realtimeMode || realtimeStream is null || realtimePlayer is null || realtimeVideo != plan.Video) return Task.CompletedTask;
        long time = realtimePlayer.Time;
        realtimeStream.Update(plan.Clips);
        // Flush the small decoder queue so mute/gain changes are heard without rebuilding media.
        if (time >= 0) { realtimePendingTime = time; realtimePlayer.Time = time; }
        return Task.CompletedTask;
    }
    private void SynchronizeVideo()
    {
        if (!realtimeMode || realtimePlayer?.IsPlaying != true || player is null || !player.IsPlaying) return;
        long now = Environment.TickCount64;
        if (now - lastCorrection < 1000 || Math.Abs(player.Time - realtimePlayer.Time) < 200) return;
        lastCorrection = now; player.Time = realtimePlayer.Time;
    }
    private void ExitRealtime(bool invalidate = true)
    {
        if (invalidate) realtimeVersion++;
        realtimeMode = false;
        realtimePlayer?.Stop(); realtimePlayer?.Dispose(); realtimePlayer = null;
        realtimeInput?.Dispose(); realtimeInput = null;
        realtimeStream?.Dispose(); realtimeStream = null; realtimeVideo = null;
    }
}
