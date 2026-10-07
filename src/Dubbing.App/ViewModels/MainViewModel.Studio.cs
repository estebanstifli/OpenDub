using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.Application.Pipeline;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool sidebarExpanded = true;
    [ObservableProperty] private int navigationIndex;
    [ObservableProperty] private int sidebarNavigationIndex;
    private bool syncingNavigation;
    partial void OnNavigationIndexChanged(int value)
    {
        syncingNavigation = true;
        try { SidebarNavigationIndex = Math.Min(3, value); }
        finally { syncingNavigation = false; }
    }
    partial void OnSidebarNavigationIndexChanged(int value)
    { if (!syncingNavigation && value >= 0) NavigationIndex = value == 3 ? 5 : value; }
    [ObservableProperty] private double timelineZoom = 1;
    [ObservableProperty] private double timelineViewportWidth = 950;
    [ObservableProperty] private bool followActivity = true;
    [ObservableProperty] private string activityText = "";
    [ObservableProperty] private bool normalizeSegments = true;
    [ObservableProperty] private bool paragraphSegments;
    [ObservableProperty] private IReadOnlyList<float> backgroundPeaks = [];
    [ObservableProperty] private string sourceTrackLabel = "Original audio";
    [ObservableProperty] private string backgroundTrackLabel = "Background · no separated track";
    private readonly Queue<string> activityLines = new();
    private string lastProgressLine = "";
    public double SidebarWidth => SidebarExpanded ? 216 : 68;
    public double TimelineWidth => TimelineViewportWidth * TimelineZoom;
    public double PlaybackTimelineOffset { get; private set; }
    public bool TrackPlayback { get; private set; } = true;
    public string ZoomLabel => $"{TimelineZoom:0.##}×";
    public string PlaybackTime => $"{TimeSpan.FromSeconds(Math.Max(0, Playhead)):mm\\:ss\\.f} / {TimeSpan.FromSeconds(Math.Max(0, VideoDuration)):mm\\:ss}";
    public event Action<double>? SeekRequested;
    public event Action? StopRequested;
    private bool followingPlayback;
    public string? VideoPath => session.Project.Source?.Path;
    public void UpdatePlaybackPosition(double seconds)
    {
        Playhead = Math.Clamp(seconds, 0, VideoDuration);
        var segment = session.Project.Segments.LastOrDefault(s => s.Start.TotalSeconds <= Playhead && s.End.TotalSeconds > Playhead);
        if (segment is null || SelectedSegment?.Id == segment.Id) return;
        followingPlayback = true;
        try { SelectedSegment = Segments.FirstOrDefault(s => s.Id == segment.Id); }
        finally { followingPlayback = false; }
    }
    partial void OnSidebarExpandedChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));
    partial void OnTimelineZoomChanged(double value) { OnPropertyChanged(nameof(TimelineWidth)); OnPropertyChanged(nameof(ZoomLabel)); }
    partial void OnTimelineViewportWidthChanged(double value) => OnPropertyChanged(nameof(TimelineWidth));
    partial void OnPlayheadChanged(double value) => OnPropertyChanged(nameof(PlaybackTime));
    partial void OnVideoDurationChanged(double value) => OnPropertyChanged(nameof(PlaybackTime));
    partial void OnStatusChanged(string value) => AppendActivity(value);
    partial void OnNormalizeSegmentsChanged(bool value) => Edit(p => p with { Settings = p.Settings with { NormalizeSegments = value } });
    partial void OnParagraphSegmentsChanged(bool value) => Edit(p => p with { Settings = p.Settings with { ParagraphSegments = value } });
    [RelayCommand] private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand] private void Navigate(string index) => NavigationIndex = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);
    [RelayCommand] private void ZoomIn() => TimelineZoom = Math.Min(16, TimelineZoom * 1.5);
    [RelayCommand] private void ZoomOut() => TimelineZoom = Math.Max(1, TimelineZoom / 1.5);
    [RelayCommand] private void FitTimeline() => TimelineZoom = 1;
    [RelayCommand] private void StopPlayback() { CancelMixUpdate(); if (preparingDub) Cancel(); DubPreviewPath = null; StopRequested?.Invoke(); Playhead = 0; }
    [RelayCommand] private void ClearActivity() { activityLines.Clear(); ActivityText = ""; }
    public void Seek(double seconds) { UpdatePlaybackPosition(seconds); SeekRequested?.Invoke(Playhead); }
    public void AttachActivity() => ActivityJournal.Entry += ReceiveActivity;
    public void DetachActivity() => ActivityJournal.Entry -= ReceiveActivity;
    private void ReceiveActivity(string line)
    {
        if (Dispatcher.UIThread.CheckAccess()) AppendActivity(line);
        else Dispatcher.UIThread.Post(() => AppendActivity(line));
    }
    private void AppendActivity(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        activityLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (activityLines.Count > 1200) activityLines.Dequeue();
        ActivityText = string.Join(Environment.NewLine, activityLines);
    }
    [RelayCommand] private async Task NormalizeDialogueAsync() => await RunAsync(ct =>
    {
        int before = session.Project.Segments.Count;
        var normalized = new SegmentNormalizer().Normalize(session.Project.Segments, ParagraphSegments);
        if (session.Project.Source is { } source) normalized = ShortSegmentTiming.Expand(normalized, source.Duration);
        Edit(p => p with { Segments = normalized }); Refresh();
        Status = L.Format("Dialogue normalized: {0} → {1} segments. Locked edits and generated audio preserved.", before, normalized.Count);
        return Task.CompletedTask;
    });
    [RelayCommand] private async Task ReviewTranscriptAsync() => await RunAsync(async ct =>
    {
        var before = session.Project.Segments;
        var reviewed = TranscriptQuality.ExcludeSuspectRepetitions(before);
        int count = reviewed.Count(s => s.ExcludedFromDubbing) - before.Count(s => s.ExcludedFromDubbing);
        Edit(p => p with { Segments = reviewed }); DubPreviewPath = null; Refresh();
        if (session.DirectoryPath is not null) await session.SaveAsync(null, ct);
        Status = L.Format("Excluded {0} suspected repetitive segments. Review original audio; uncheck Exclude from dubbing to restore.", count);
    });
}
