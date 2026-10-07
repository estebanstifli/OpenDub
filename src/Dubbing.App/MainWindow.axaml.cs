using Avalonia.Controls;
using Dubbing.App.ViewModels;
using LibVLCSharp.Shared;
using Avalonia.Threading;

namespace Dubbing.App;

public partial class MainWindow : Window
{
    private bool closingApproved;
    private bool checkingClose;
    private LibVLC? vlc;
    private MediaPlayer? player;
    private MainViewModel? attached;
    private readonly DispatcherTimer playbackClock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string? playbackPath;
    private bool pauseOnStart;
    private bool seekLoad;
    private long pendingTime;
    private double timelineOffset;
    private bool trackTime;
    private int playbackVersion;
    private bool loadingMedia;
    private int? pendingAudioTrack;
    public MainWindow()
    {
        InitializeComponent();
        VideoSurface.Mirror = SegmentVideo;
        Timeline.SeekRequested += seconds => attached?.Seek(seconds);
        Timeline.SegmentSelected += id => { if (attached is not null) attached.SelectedSegment = attached.Segments.FirstOrDefault(s => s.Id == id); };
        TimelineScroller.SizeChanged += (_, e) => { if (attached is not null) attached.TimelineViewportWidth = Math.Max(400, e.NewSize.Width - 2); };
        ActivityBox.TextChanged += (_, _) => { if (attached?.FollowActivity == true) ActivityBox.CaretIndex = ActivityBox.Text?.Length ?? 0; };
        playbackClock.Tick += (_, _) =>
        {
            if (attached is null || player is null || !player.IsPlaying || !trackTime || Timeline.IsDragging) return;
            attached.UpdatePlaybackPosition(timelineOffset + player.Time / 1000d);
            double x = Timeline.Gutter + attached.Playhead / Math.Max(1, attached.VideoDuration) * (Timeline.Bounds.Width - Timeline.Gutter - 8);
            if (x > TimelineScroller.Offset.X + TimelineScroller.Viewport.Width - 20 || x < TimelineScroller.Offset.X)
                TimelineScroller.Offset = new Avalonia.Vector(Math.Max(0, x - TimelineScroller.Viewport.Width * .25), 0);
        };
        playbackClock.Start();
        Opened += async (_, _) => { LoadProjectVideo(); if (DataContext is MainViewModel model) await model.InitializeWorkspaceAsync(); };
        DataContextChanged += (_, _) =>
        {
            if (attached is not null) { attached.PropertyChanged -= ViewModelChanged; attached.PlaybackRequested -= Play; attached.PauseRequested -= Pause; attached.SeekRequested -= Seek; attached.StopRequested -= Stop; attached.DetachActivity(); }
            attached = DataContext as MainViewModel;
            if (attached is not null) { attached.PropertyChanged += ViewModelChanged; attached.PlaybackRequested += Play; attached.PauseRequested += Pause; attached.SeekRequested += Seek; attached.StopRequested += Stop; attached.AttachActivity(); if (IsVisible) LoadProjectVideo(); }
        };
        Closed += (_, _) => { playbackClock.Stop(); attached?.CancelMixUpdate(); attached?.DetachActivity(); attached?.StopAutoSave(); player?.Stop(); player?.Dispose(); player = null; VideoSurface.Dispose(); vlc?.Dispose(); vlc = null; };
        Closing += async (_, args) =>
        {
            if (closingApproved || DataContext is not MainViewModel model) return;
            args.Cancel = true;
            if (checkingClose) return;
            checkingClose = true;
            try
            {
                if (await model.CanCloseAsync()) { closingApproved = true; Close(); }
            }
            finally { checkingClose = false; }
        };
    }
    private void SegmentTextFocused(object? sender, Avalonia.Input.GotFocusEventArgs e)
    {
        if (sender is TextBox { DataContext: SegmentViewModel row } && attached is not null && attached.SelectedSegment != row)
            attached.SelectedSegment = row;
    }
    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.VideoPath)) LoadProjectVideo(); }
    private void LoadProjectVideo()
    {
        if (attached?.VideoPath is not { } path) { Stop(); VideoSurface.ClearFrame(); return; }
        if (path == playbackPath) return;
        seekLoad = true;
        try { attached.PlayVideoCommand.Execute(null); }
        finally { seekLoad = false; }
    }
    private void Pause() { if (attached is not null) attached.IsPlaybackRunning = false; player?.SetPause(true); }
    private void Stop() { if (attached is not null) attached.IsPlaybackRunning = false; playbackVersion++; loadingMedia = false; player?.Stop(); playbackPath = null; }
    private void Seek(double seconds)
    {
        if (player is not null && playbackPath is not null && trackTime && seconds >= timelineOffset &&
            (player.Length <= 0 || seconds <= timelineOffset + player.Length / 1000d))
        {
            pendingTime = (long)(Math.Max(0, seconds - timelineOffset) * 1000); player.Time = pendingTime;
            if (!player.IsPlaying) player.NextFrame();
        }
        else if (attached is not null) { seekLoad = player?.IsPlaying != true; try { attached.PlayVideoCommand.Execute(null); } finally { seekLoad = false; } }
    }
    private async void Play(string path, TimeSpan offset)
    {
        int version = ++playbackVersion;
        try
        {
            if (vlc is null)
            {
                LibVLCSharp.Shared.Core.Initialize(Services.PackageSelfCheck.VlcDirectory());
                vlc = new LibVLC("--no-video-title-show", "--vout=vmem", "--avcodec-hw=none"); player = new MediaPlayer(vlc);
                VideoSurface.Attach(player);
                player.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (player is null) return;
                    ConfigurePlaybackAudio();
                    player.Time = pendingTime;
                    if (pauseOnStart) { player.SetPause(true); player.NextFrame(); pauseOnStart = false; }
                    player.Mute = false;
                    if (attached is not null) attached.IsPlaybackRunning = player.IsPlaying;
                });
                player.ESAdded += (_, _) => Dispatcher.UIThread.Post(ConfigurePlaybackAudio);
                player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => { if (attached is not null) attached.IsPlaybackRunning = false; });
                player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => { if (attached is not null) attached.Status = attached.L["Playback failed. Check the media file and VLC installation."]; });
            }
            pauseOnStart = seekLoad; pendingTime = (long)offset.TotalMilliseconds; timelineOffset = attached?.PlaybackTimelineOffset ?? 0; trackTime = attached?.TrackPlayback ?? true;
            player!.Mute = seekLoad;
            player.Volume = 100;
            // Source and content-keyed dub previews are immutable; other generated paths may be overwritten.
            if (!loadingMedia && playbackPath == path && (path == attached?.VideoPath || path == attached?.DubPreviewPath)) { player!.Time = pendingTime; player.Play(); return; }
            player!.Stop(); playbackPath = path; loadingMedia = true;
            Dubbing.Infrastructure.Processes.ActivityJournal.Write($"Playback · {Path.GetFileName(path)} · refreshed media · {offset.TotalSeconds:F2} s");
            using var media = new LibVLCSharp.Shared.Media(vlc, new Uri(Path.GetFullPath(path)));
            await media.Parse(MediaParseOptions.ParseLocal, 5000);
            if (version != playbackVersion || player is null) return;
            uint width = 0, height = 0;
            foreach (var track in media.Tracks.Where(t => t.TrackType == TrackType.Video))
            { width = track.Data.Video.Width; height = track.Data.Video.Height; break; }
            VideoSurface.ConfigureSource(width, height);
            pendingAudioTrack = path == attached?.VideoPath && attached.SourceAudioOrdinal >= 0
                ? media.Tracks.Where(t => t.TrackType == TrackType.Audio).Select(t => (int?)t.Id).ElementAtOrDefault(attached.SourceAudioOrdinal)
                : media.Tracks.Where(t => t.TrackType == TrackType.Audio).Select(t => (int?)t.Id).FirstOrDefault();
            loadingMedia = false;
            media.AddOption(":start-time=" + (pendingTime / 1000d).ToString(System.Globalization.CultureInfo.InvariantCulture));
            player.Play(media);
        }
        catch (Exception ex) { if (version == playbackVersion) { loadingMedia = false; playbackPath = null; if (attached is not null) attached.Status = attached.L.Format("Playback unavailable: {0}", ex.Message); } }
    }
    private void ConfigurePlaybackAudio()
    {
        if (player is null) return;
        player.Volume = 100;
        player.Mute = false;
        var tracks = player.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
        if (tracks.Length == 0) return;
        int id = pendingAudioTrack is { } desired && tracks.Any(t => t.Id == desired) ? desired : tracks[0].Id;
        if (player.AudioTrack != id) player.SetAudioTrack(id);
    }
}
