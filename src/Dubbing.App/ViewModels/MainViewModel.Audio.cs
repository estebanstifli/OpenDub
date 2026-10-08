using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.App.Services;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    public int SourceAudioOrdinal => Array.FindIndex(session.Project.AudioTracks.OrderBy(t => t.Index).ToArray(), t => t.Index == session.Project.Settings.AnalysisStreamIndex);
    [ObservableProperty] private bool originalVoiceMuted = true;
    [ObservableProperty] private bool dubMuted;
    [ObservableProperty] private bool backgroundMuted;
    [ObservableProperty] private double originalVoiceGain = 1;
    [ObservableProperty] private string audioTrackSummary = "";
    [ObservableProperty] private string audioPreparationStatus = "";
    public string OriginalTrackName => L[session.Project.Stems.ContainsKey("vocals") ? "Original dialogue" : "Original audio (full mix)"];
    public string DubTrackName => L[session.Project.Segments.Any(s => !s.ExcludedFromDubbing && (s.GeneratedSpeech is not null || s.FittedSpeech is not null)) ? "Dubbed dialogue" : "Dubbed dialogue (not generated)"];
    public bool IsPlaybackRunning { get; set; }
    public string OriginalSpeakerIcon => OriginalVoiceMuted ? "volume-x" : "volume-2";
    public string DubSpeakerIcon => DubMuted ? "volume-x" : "volume-2";
    public string BackgroundSpeakerIcon => BackgroundMuted ? "volume-x" : "volume-2";
    public void CancelMixUpdate() => realtimeMixVersion++;

    private void ChangeMix(Func<ProjectSettings, ProjectSettings> change)
    {
        if (refreshing) return;
        DubPreviewPath = null;
        Edit(p => p with { Settings = change(p.Settings), Segments = MixDirty(p) });
        _ = UpdateRealtimeMixAsync();
    }
    private static IReadOnlyList<DubSegment> MixDirty(DubbingProject p) => p.Segments.Select(s => s with
        { DirtyStages = s.DirtyStages.Union([PipelineStage.Mix, PipelineStage.Mux]).Order().ToArray() }).ToArray();
    partial void OnOriginalVoiceMutedChanged(bool value) { OnPropertyChanged(nameof(OriginalSpeakerIcon)); ChangeMix(s => s with { OriginalVoiceMuted = value }); }
    partial void OnDubMutedChanged(bool value) { OnPropertyChanged(nameof(DubSpeakerIcon)); ChangeMix(s => s with { DubMuted = value }); }
    partial void OnBackgroundMutedChanged(bool value) { OnPropertyChanged(nameof(BackgroundSpeakerIcon)); ChangeMix(s => s with { BackgroundMuted = value }); }
    partial void OnOriginalVoiceGainChanged(double value) => ChangeMix(s => s with { OriginalVoiceGain = value });

    private void RefreshAudioControls()
    {
        var p = session.Project;
        OnPropertyChanged(nameof(OriginalTrackName));
        OnPropertyChanged(nameof(DubTrackName));
        OriginalVoiceMuted = p.Settings.OriginalVoiceMuted; DubMuted = p.Settings.DubMuted;
        BackgroundMuted = p.Settings.BackgroundMuted; OriginalVoiceGain = p.Settings.OriginalVoiceGain;
        AudioTrackSummary = string.Join(Environment.NewLine, p.AudioTracks.Select(t =>
            $"#{t.Index} · {t.Language ?? "und"} · {t.Title ?? ""} · {t.Codec} · {t.ChannelLayout ?? t.Channels.ToString()} · {L[t.Role.ToString()]}"));
        AudioPreparationStatus = L[p.Stems.Keys.Any(ProjectAudioMix.IsBackground) ? "Clean background available." : "No clean background. Configure tracks and prepare audio to retain music and effects."];
    }

    private async Task<AudioTrackChoice?> ChooseTracksAsync(string input, IReadOnlyList<SourceAudioTrack> saved, int? selected, bool separate, CancellationToken ct, Func<AudioTrackChoice, Task>? process = null)
    {
        if (workspace is null) return null;
        var info = await workspace.Media.ProbeAsync(input, ct);
        var tracks = AudioPreparationService.Discover(info, saved);
        if (tracks.Count == 0) throw new InvalidOperationException("The video contains no audio track.");
        int preferred = selected ?? tracks.First(t => t.Role is AudioTrackRole.Mixed or AudioTrackRole.Dialogue).Index;
        string temp = Path.Combine(Path.GetTempPath(), "dubbing-track-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previews = new List<Task>();
        async Task Preview(int index, double seconds)
        {
            StopRequested?.Invoke(); DubPreviewPath = null;
            var track = tracks.Single(t => t.Index == index);
            string audio = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".wav");
            await workspace.Media.ExtractTrackAsync(input, index, audio, track.SampleRate, track.Channels, null, previewCancellation.Token,
                TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, info.Duration.TotalSeconds - .1))), TimeSpan.FromSeconds(15));
            previewCancellation.Token.ThrowIfCancellationRequested();
            TrackPlayback = false; PlaybackRequested?.Invoke(audio, TimeSpan.Zero);
        }
        try
        {
            return await dialogs.ChooseAudioTracksAsync(tracks, preferred, separate,
                (index, seconds) => { var task = Preview(index, seconds); previews.Add(task); return task; }, () => StopRequested?.Invoke(), process, () => operation?.Cancel());
        }
        finally
        {
            previewCancellation.Cancel();
            try { await Task.WhenAll(previews); } catch (Exception) { /* Dialog reports audition errors. */ }
            StopRequested?.Invoke(); Directory.Delete(temp, true);
        }
    }

    [RelayCommand] private async Task ConfigureAudioTracksAsync() => await RunAsync(async ct =>
    {
        var source = session.Project.Source ?? throw new InvalidOperationException("Import a video first.");
        var choice = await ChooseTracksAsync(source.Path, session.Project.AudioTracks, session.Project.Settings.AnalysisStreamIndex, session.Project.Settings.UseSeparation, ct);
        if (choice is null) return;
        Edit(p => p with { AudioTracks = choice.Tracks, SelectedSourceAudio = null, AnalysisAudio = null, Stems = new Dictionary<string, Dubbing.Core.Engines.AudioReference>(),
            Settings = p.Settings with { AnalysisStreamIndex = choice.AnalysisIndex, UseSeparation = choice.Separate }, Segments = MixDirty(p) });
        DubPreviewPath = null; Refresh();
        if (session.DirectoryPath is not null) await session.SaveAsync(null, ct);
        Status = L["Track selection saved. Prepare audio next. Existing segments are preserved; use Reanalyze only if you want a new transcription."];
    });
    [RelayCommand] private async Task PrepareAudioAsync() => await RunAsync(async ct =>
    {
        if (workspace is null || !await SaveCoreAsync(ct)) return;
        var prepared = await new AudioPreparationService(workspace.Media, registry).PrepareAsync(session.Project, session.DirectoryPath!, ReportProgress(), ct);
        session.Update(prepared with { Segments = MixDirty(prepared) });
        await session.SaveAsync(null, ct); DubPreviewPath = null; Refresh(); await LoadWaveformAsync(ct);
        Status = L["Audio prepared. Segments, translations and generated speech were preserved."];
    });
    [RelayCommand] private async Task ListenOriginalVoiceAsync() => await ListenMixAsync(AudioAudition.OriginalVoice);
    [RelayCommand] private async Task ListenDubAsync() => await ListenMixAsync(AudioAudition.Dub);
    [RelayCommand] private async Task ListenBackgroundAsync() => await ListenMixAsync(AudioAudition.Background);
    private async Task ListenMixAsync(AudioAudition audition) => await RunAsync(async ct =>
    {
        if (workspace is null || session.DirectoryPath is not { } directory) return;
        preparingDub = true;
        StopRequested?.Invoke();
        try
        {
            if (!await ReviewPlaybackOverlapsAsync(ct)) return;
            var plan = await PlaybackPlanAsync(ct, audition);
            ct.ThrowIfCancellationRequested();
            DubPreviewPath = null; TrackPlayback = true; PlaybackTimelineOffset = 0;
            if (RealtimePlaybackRequested is not null) await RealtimePlaybackRequested(plan, TimeSpan.FromSeconds(Playhead >= VideoDuration ? 0 : Playhead));
        }
        finally { preparingDub = false; }
    });
}
