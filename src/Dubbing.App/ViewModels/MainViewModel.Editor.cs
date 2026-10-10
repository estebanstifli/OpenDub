using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    public SegmentEditorViewModel Editor { get; } = new();
    public ObservableCollection<SpeakerProfile> Speakers { get; } = [];
    public IReadOnlyList<EngineDescriptor> VoiceEngines => registry.GetAvailableEngines(EngineTask.Tts);
    [ObservableProperty] private SegmentViewModel? selectedSegment;
    [ObservableProperty] private SpeakerProfile? selectedSpeaker;
    [ObservableProperty] private SpeakerProfile? mergeTarget;
    [ObservableProperty] private string speakerName = "";
    [ObservableProperty] private string referencePath = "";
    [ObservableProperty] private bool useSeparation;
    [ObservableProperty] private bool useAlignment;
    [ObservableProperty] private bool automaticReferences = true;
    [ObservableProperty] private bool automaticEmotion = true;
    [ObservableProperty] private double voiceGain = 1;
    [ObservableProperty] private double backgroundGain = 1;
    [ObservableProperty] private double durationTolerance = 0.05;
    [ObservableProperty] private double maximumTimeStretch = 0.05;
    [ObservableProperty] private string exportContainer = "mkv";
    [ObservableProperty] private string glossaryJson = "{}";
    [ObservableProperty] private IReadOnlyList<DubSegment> timelineSegments = [];
    [ObservableProperty] private IReadOnlyList<float> waveformPeaks = [];
    [ObservableProperty] private double videoDuration = 1;
    [ObservableProperty] private double playhead;
    public IReadOnlyList<string> ExportContainers { get; } = ["mkv", "mp4"];
    public event Action<string, TimeSpan>? PlaybackRequested;
    public event Action? PauseRequested;
    private CancellationTokenSource? autoSave;
    private bool loadingSegment;
    private bool pendingSegmentEdits;
    private bool applyingAutoSave;
    private SegmentEditorViewModel? loadedEditor;
    public void StopAutoSave() { autoSave?.Cancel(); autoSave?.Dispose(); autoSave = null; }

    partial void OnSelectedSegmentChanged(SegmentViewModel? value)
    {
        foreach (var row in Segments) row.IsActive = row.Id == value?.Id;
        LoadSegment(); NotifySegmentTools(); OnPropertyChanged(nameof(SelectedSegmentTitle));
        if (!refreshing && !followingPlayback && session.Project.Segments.FirstOrDefault(s => s.Id == value?.Id) is { } segment)
            Seek(segment.Start.TotalSeconds);
    }
    partial void OnSelectedSegmentChanging(SegmentViewModel? value)
    {
        if (pendingSegmentEdits && !refreshing)
        {
            try { ApplySegmentCore(refresh: false); }
            catch (Exception ex) { Status = L.Format("Operation failed: {0}", ex.Message); }
        }
    }
    partial void OnSelectedSpeakerChanged(SpeakerProfile? value)
    { SpeakerName = value?.DisplayName ?? ""; ReferencePath = value?.VoiceReference?.Path ?? ""; RefreshSpeakerSegments(); }
    partial void OnUseSeparationChanged(bool value)
    {
        if (refreshing) return;
        Edit(p => p with { Settings = p.Settings with { UseSeparation = value }, Stems = new Dictionary<string, AudioReference>(),
            SelectedSourceAudio = null, AnalysisAudio = null, Segments = MixDirty(p) });
        DubPreviewPath = null;
        AudioPreparationStatus = L["Separation settings changed. Prepare audio to update the mix."];
    }
    partial void OnUseAlignmentChanged(bool value) => Edit(p => ProjectEditor.Invalidate(p with { Settings = p.Settings with { UseAlignment = value } }, PipelineStage.Alignment));
    partial void OnAutomaticReferencesChanged(bool value) => Edit(p => p with { Settings = p.Settings with { AutomaticVoiceReferences = value } });
    partial void OnAutomaticEmotionChanged(bool value) => Edit(p => ProjectEditor.Invalidate(p with { Settings = p.Settings with { AutomaticEmotion = value } }, PipelineStage.Tts));
    partial void OnVoiceGainChanged(double value) => ChangeMix(s => s with { VoiceGain = value });
    partial void OnBackgroundGainChanged(double value) => ChangeMix(s => s with { BackgroundGain = value });
    partial void OnDurationToleranceChanged(double value) => Edit(p => p with { Settings = p.Settings with { DurationTolerance = value } });
    partial void OnMaximumTimeStretchChanged(double value) => Edit(p => ProjectEditor.Invalidate(p with { Settings = p.Settings with { MaximumTimeStretch = value } }, PipelineStage.DurationFit));
    partial void OnExportContainerChanged(string value) => Edit(p => p with { Settings = p.Settings with { ExportContainer = value } });

    private void RefreshEditor()
    {
        string? segmentId = SelectedSegment?.Id, speakerId = SelectedSpeaker?.Id;
        Speakers.Clear(); foreach (var speaker in session.Project.Speakers) Speakers.Add(speaker);
        SelectedSpeaker = Speakers.FirstOrDefault(s => s.Id == speakerId) ?? Speakers.FirstOrDefault();
        RefreshSpeakerSegments();
        SelectedSegment = Segments.FirstOrDefault(s => s.Id == segmentId) ?? Segments.FirstOrDefault();
        var settings = session.Project.Settings;
        UseSeparation = settings.UseSeparation; UseAlignment = settings.UseAlignment;
        AutomaticReferences = settings.AutomaticVoiceReferences; AutomaticEmotion = settings.AutomaticEmotion;
        VoiceGain = settings.VoiceGain; BackgroundGain = settings.BackgroundGain; DurationTolerance = settings.DurationTolerance;
        MaximumTimeStretch = settings.MaximumTimeStretch; ExportContainer = settings.ExportContainer;
        GlossaryJson = JsonSerializer.Serialize(settings.Glossary, new JsonSerializerOptions { WriteIndented = true });
        TimelineSegments = session.Project.Segments; VideoDuration = session.Project.Source?.Duration.TotalSeconds ?? 8;
        OnPropertyChanged(nameof(VideoPath));
        if (session.Project.Source is null)
        {
            WaveformPeaks = []; BackgroundPeaks = [];
            SourceTrackLabel = L["Original audio"]; BackgroundTrackLabel = L["No separated background track"];
        }
        OnPropertyChanged(nameof(VoiceEngines)); OnPropertyChanged(nameof(SelectedSegmentTitle)); LoadSegment();
    }
    private void LoadSegment()
    {
        loadingSegment = true;
        try
        {
        var segment = session.Project.Segments.FirstOrDefault(s => s.Id == SelectedSegment?.Id);
        if (segment is null) return;
        Editor.SourceText = segment.Source.Text; Editor.TranslationText = segment.Translation.Text;
        Editor.ExcludedFromDubbing = segment.ExcludedFromDubbing;
        Editor.ClipAudioToSlot = segment.ClipAudioToSlot;
        Editor.StartSeconds = segment.Start.TotalSeconds; Editor.EndSeconds = segment.End.TotalSeconds;
        Editor.Speaker = Speakers.FirstOrDefault(s => s.Id == segment.SpeakerId);
        string? engineId = segment.Tts.EngineId ?? Editor.Speaker?.PreferredTtsEngine ?? session.Project.Engines.Tts;
        Editor.VoiceEngine = VoiceEngines.FirstOrDefault(e => e.EngineId == engineId);
        Editor.EmotionMode = segment.Emotion.Mode == EmotionMode.None && session.Project.Settings.AutomaticEmotion && Editor.VoiceEngine?.Capabilities.SupportsEmotionReferenceAudio == true ? EmotionMode.SourceAudio : segment.Emotion.Mode; Editor.EmotionText = segment.Emotion.Instruction ?? segment.Emotion.Label ?? "";
        Editor.Intensity = segment.Emotion.Intensity ?? 0.75; Editor.EmotionReference = segment.Emotion.ReferenceAudio?.Path ?? "";
        Editor.EmotionVector = string.Join(',', segment.Emotion.Vector ?? new double[8]);
        Editor.Speed = segment.Tts.SpeakingRate ?? 1; Editor.Seed = segment.Tts.Seed?.ToString() ?? "";
        Editor.AdvancedOptions = JsonSerializer.Serialize(segment.Tts.EngineOptions, new JsonSerializerOptions { WriteIndented = true });
        var speech = segment.FittedSpeech ?? segment.GeneratedSpeech;
        Editor.DurationSummary = speech is null ? "" : $"{speech.Duration.TotalSeconds:F2} / {segment.TargetDuration.TotalSeconds:F2} s · {(speech.Duration.TotalSeconds / segment.TargetDuration.TotalSeconds - 1):+0.0%;-0.0%;0.0%}";
        loadedEditor = new(); loadedEditor.CopyFrom(Editor);
        }
        finally { loadingSegment = false; pendingSegmentEdits = false; }
    }
    private void ApplySegmentCore(bool refresh = true)
    {
        var previous = session.Project.Segments.Single(s => s.Id == (SelectedSegment?.Id ?? throw new InvalidOperationException("Select a segment.")));
        if (!double.IsFinite(Editor.StartSeconds) || !double.IsFinite(Editor.EndSeconds) || Editor.StartSeconds < 0 || Editor.EndSeconds <= Editor.StartSeconds)
            throw new ArgumentException("Segment end must be after its start.");
        if (session.Project.Source is { } source && Editor.EndSeconds > source.Duration.TotalSeconds + 0.02)
            throw new ArgumentException("Segment end exceeds the video duration.");
        var options = JsonSerializer.Deserialize<Dictionary<string, object?>>(Editor.AdvancedOptions) ?? [];
        bool placementOnly = loadedEditor is { } old && Editor.SourceText == old.SourceText && Editor.TranslationText == old.TranslationText &&
            Editor.Speaker?.Id == old.Speaker?.Id && Editor.VoiceEngine?.EngineId == old.VoiceEngine?.EngineId &&
            Editor.EmotionMode == old.EmotionMode && Editor.EmotionText == old.EmotionText && Editor.Intensity == old.Intensity &&
            Editor.EmotionReference == old.EmotionReference && Editor.EmotionVector == old.EmotionVector && Editor.Speed == old.Speed &&
            Editor.Seed == old.Seed && Editor.AdvancedOptions == old.AdvancedOptions;
        if (placementOnly && TimeSpan.FromSeconds(Editor.StartSeconds) == previous.Start && TimeSpan.FromSeconds(Editor.EndSeconds) == previous.End &&
            Editor.ClipAudioToSlot == previous.ClipAudioToSlot && Editor.ExcludedFromDubbing == previous.ExcludedFromDubbing)
        { pendingSegmentEdits = false; return; }
        bool translationChanged = Editor.TranslationText != previous.Translation.Text;
        var emotion = new EmotionDescriptor { Mode = Editor.EmotionMode, Label = Editor.EmotionMode == EmotionMode.Label ? Editor.EmotionText : null,
            Instruction = Editor.EmotionMode == EmotionMode.Instruction ? Editor.EmotionText : null, Intensity = Editor.Intensity,
            ReferenceAudio = Editor.EmotionMode == EmotionMode.ReferenceAudio ? new(Editor.EmotionReference) : null,
            Vector = Editor.EmotionMode == EmotionMode.Vector ? Editor.EmotionVector.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null };
        var updated = previous with { Start = TimeSpan.FromSeconds(Editor.StartSeconds), End = TimeSpan.FromSeconds(Editor.EndSeconds), SpeakerId = Editor.Speaker?.Id,
            Source = previous.Source with { Text = Editor.SourceText },
            ExcludedFromDubbing = Editor.ExcludedFromDubbing,
            ClipAudioToSlot = Editor.ClipAudioToSlot,
            Translation = new(session.Project.Settings.TargetLanguage, Editor.TranslationText, previous.Translation.Locked || translationChanged),
            Emotion = emotion, Tts = previous.Tts with { EngineId = Editor.VoiceEngine?.EngineId,
                SpeakingRate = Editor.SupportsSpeed ? Editor.Speed : null, Seed = Editor.SupportsSeed && ulong.TryParse(Editor.Seed, out var seed) ? seed : null, EngineOptions = options },
            DirtyStages = previous.DirtyStages.Append(PipelineStage.Tts).Union(StageDependencies.After(PipelineStage.Tts)).ToArray(), Status = SegmentStatus.TtsDirty };
        if (translationChanged) updated = updated with { DirtyStages = updated.DirtyStages.Where(s => s != PipelineStage.Translation).ToArray() };
        if (previous.Source.Text != updated.Source.Text && !updated.Translation.Locked) updated = updated with { DirtyStages = updated.DirtyStages.Append(PipelineStage.Translation).Distinct().ToArray() };
        if (placementOnly)
        {
            bool lengthChanged = updated.TargetDuration != previous.TargetDuration;
            updated = previous with { Start = updated.Start, End = updated.End, ClipAudioToSlot = Editor.ClipAudioToSlot, ExcludedFromDubbing = Editor.ExcludedFromDubbing,
                SourceReferenceStart = previous.SourceReferenceStart ?? previous.Start, SourceReferenceEnd = previous.SourceReferenceEnd ?? previous.End,
                DirtyStages = previous.DirtyStages.Union(lengthChanged ? new[] { PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux } : new[] { PipelineStage.Mix, PipelineStage.Mux }).ToArray() };
        }
        if (updated.Start != previous.Start || updated.End != previous.End ||
            updated.ClipAudioToSlot != previous.ClipAudioToSlot || previous.ExcludedFromDubbing && !updated.ExcludedFromDubbing)
        {
            ResetTimingReviewsForProject(); pendingTimingReviews.Add(updated.Id);
        }
        Edit(p => p with { Segments = p.Segments.Select(s => s.Id == updated.Id ? updated : s).ToArray() });
        pendingSegmentEdits = false;
        loadedEditor = new(); loadedEditor.CopyFrom(Editor);
        if (refresh) Refresh();
        else
        {
            refreshing = true;
            try
            {
                if (SelectedSegment is { } row)
                {
                    row.SourceText = updated.Source.Text; row.TranslationText = updated.Translation.Text;
                    row.Timing = $"{updated.Start:hh\\:mm\\:ss\\.fff} → {updated.End:hh\\:mm\\:ss\\.fff}";
                    row.Speaker = Editor.Speaker?.DisplayName ?? L["Unknown speaker"];
                    row.State = L[updated.Status.ToString()];
                }
                TimelineSegments = session.Project.Segments;
            }
            finally { refreshing = false; }
        }
    }
    [RelayCommand] private async Task ApplySegmentAsync() => await RunAsync(ct => { ApplySegmentCore(); return Task.CompletedTask; });
    [RelayCommand] private async Task ExcludeUnassignedSegmentsAsync() => await RunAsync(ct =>
    {
        Edit(p => p with { Segments = p.Segments.Select(s => s.SpeakerId is null ? s with { ExcludedFromDubbing = true } : s).ToArray() });
        Refresh(); Status = L["Segments without speakers excluded. Review the selection; you can re-enable each segment."];
        return Task.CompletedTask;
    });
    [RelayCommand] private async Task UnlockAsync() => await RunAsync(ct =>
    { if (SelectedSegment is not null) Edit(p => ProjectEditor.UnlockTranslation(p, SelectedSegment.Id)); Refresh(); return Task.CompletedTask; });
    [RelayCommand] private async Task RegenerateAsync() => await RunAsync(ct => RegenerateCoreAsync(false, ct));
    [RelayCommand] private async Task RegenerateAndFitAsync() => await RunAsync(ct => RegenerateCoreAsync(true, ct));
    private async Task RegenerateCoreAsync(bool fitDuration, CancellationToken ct)
    {
        if (SelectedSegment is null) return;
        StopRequested?.Invoke(); DubPreviewPath = null;
        AppendActivity(L[fitDuration ? "Regenerating speech with duration adjustment; translation may be rewritten." : "Regenerating speech only; keeping the current translation."]);
        ApplySegmentCore(); string id = SelectedSegment!.Id;
        if (!await SaveCoreAsync(ct)) return;
        try { await speech.GenerateAsync(session.Project, session.DirectoryPath!, ReportProgress(), ct, id, fitDuration, overwriteExisting: true); }
        finally { await session.OpenAsync(session.DirectoryPath!, CancellationToken.None); Refresh(); }
        await ResolveAffectedCollisionsAsync(ct, [id]);
    }
    [RelayCommand] private async Task ReanalyzeAsync() => await RunAsync(async ct =>
    {
        var source = session.Project.Source ?? throw new InvalidOperationException("Import a video first.");
        if (!await SaveCoreAsync(ct)) return;
        try { await importer.ImportAsync(session.Project, source.Path, session.DirectoryPath!, ReportProgress(), ct); }
        finally { await session.OpenAsync(session.DirectoryPath!, CancellationToken.None); Refresh(); }
        await LoadWaveformAsync(ct);
    });
    [RelayCommand] private void RenameSpeaker()
    {
        if (SelectedSpeaker is null || string.IsNullOrWhiteSpace(SpeakerName)) return;
        Edit(p => p with { Speakers = p.Speakers.Select(s => s.Id == SelectedSpeaker.Id ? s with { DisplayName = SpeakerName } : s).ToArray() }); Refresh();
    }
    [RelayCommand] private void NewSpeaker()
    { Edit(p => p with { Speakers = p.Speakers.Append(new SpeakerProfile { DisplayName = "Speaker " + (p.Speakers.Count + 1) }).ToArray() }); Refresh(); }
    [RelayCommand] private void MergeSpeakers()
    {
        if (SelectedSpeaker is null || MergeTarget is null || SelectedSpeaker.Id == MergeTarget.Id) return;
        string from = SelectedSpeaker.Id, to = MergeTarget.Id;
        Edit(p => p with { Speakers = p.Speakers.Where(s => s.Id != from).ToArray(), Segments = p.Segments.Select(s => s.SpeakerId == from ? s with
            { SpeakerId = to, DirtyStages = s.DirtyStages.Append(PipelineStage.Tts).Union(StageDependencies.After(PipelineStage.Tts)).ToArray() } : s).ToArray() }); Refresh();
    }
    [RelayCommand] private async Task ImportReferenceAsync() => await RunAsync(async ct =>
    {
        if (SelectedSpeaker is null || !await SaveCoreAsync(ct)) return;
        string? file = await dialogs.PickFileAsync("Import reference"); if (file is null) return;
        var info = await workspace!.Media.ProbeAsync(file, ct);
        if (info.Duration < TimeSpan.FromSeconds(1))
            throw new InvalidOperationException(L.Format("Voice reference for {0} is {1:F2} s. Choose at least 1 second.", SelectedSpeaker.DisplayName, info.Duration.TotalSeconds));
        string relative = $"assets/voices/{Guid.NewGuid():N}.wav";
        await workspace!.Media.ExtractAudioAsync(file, Path.Combine(session.DirectoryPath!, relative), 24000, 1, ReportProgress(), ct);
        UpdateVoiceReference(new(relative));
    });
    [RelayCommand] private Task ChooseReferenceAsync() => SelectedSegment is null ? Task.CompletedTask : SelectSpeakerSegmentAsync(SelectedSegment.Id);
    private void UpdateVoiceReference(AudioReference reference, string? segmentId = null, double? sourceStart = null, double? sourceEnd = null)
    {
        string id = SelectedSpeaker!.Id;
        Edit(p => p with { Speakers = p.Speakers.Select(s => s.Id == id ? s with { VoiceReference = reference, VoiceReferenceIsAutomatic = false, VoiceReferenceSegmentId = segmentId, VoiceReferenceSourceStart = sourceStart, VoiceReferenceSourceEnd = sourceEnd } : s).ToArray(),
            Segments = p.Segments.Select(s => s.SpeakerId == id ? s with { DirtyStages = s.DirtyStages.Append(PipelineStage.Tts).Union(StageDependencies.After(PipelineStage.Tts)).ToArray() } : s).ToArray() }); Refresh();
    }
    public string? DubPreviewPath { get; private set; }
    private bool preparingDub;
    [RelayCommand] private async Task PlayAsync()
    {
        if (IsBusy) return;
        await PlayDubAsync();
    }
    [RelayCommand] private void PlayVideo() { if (preparingDub) Cancel(); DubPreviewPath = null; PlaybackTimelineOffset = 0; TrackPlayback = true; if (session.Project.Source is { } source) PlaybackRequested?.Invoke(source.Path, TimeSpan.FromSeconds(Playhead)); }
    [RelayCommand] private void Pause() { CancelMixUpdate(); PauseRequested?.Invoke(); }
    private async Task PlayDubAsync() => await RunAsync(async ct =>
    {
        if (workspace is null || session.DirectoryPath is not { } directory) return;
        preparingDub = true;
        try
        {
            if (!await ReviewPlaybackOverlapsAsync(ct)) return;
            var plan = await PlaybackPlanAsync(ct);
            ct.ThrowIfCancellationRequested();
            DubPreviewPath = null; PlaybackTimelineOffset = 0; TrackPlayback = true;
            if (RealtimePlaybackRequested is not null) await RealtimePlaybackRequested(plan, TimeSpan.FromSeconds(Playhead >= VideoDuration ? 0 : Playhead));
        }
        finally { preparingDub = false; }
    });
    [RelayCommand] private void PreviewReference()
    { DubPreviewPath = null; if (SelectedSpeaker?.VoiceReference is { } reference && session.DirectoryPath is { } directory) { TrackPlayback = false; PlaybackRequested?.Invoke(Path.Combine(directory, reference.Path), TimeSpan.Zero); } }
    private async Task LoadWaveformAsync(CancellationToken ct)
    {
        WaveformPeaks = []; BackgroundPeaks = [];
        if (workspace is null || session.DirectoryPath is null) return;
        var stems = session.Project.Stems;
        bool hasVocals = stems.TryGetValue("vocals", out var vocals);
        string audio = hasVocals ? Path.Combine(session.DirectoryPath, vocals!.Path) : Path.Combine(session.DirectoryPath, session.Project.AnalysisAudio?.Path ?? "assets/extracted/analysis.wav");
        if (!File.Exists(audio) && session.Project.Source is { } source) { audio = source.Path; hasVocals = false; }
        SourceTrackLabel = L[hasVocals ? "Voice" : "Original audio"];
        BackgroundTrackLabel = L["No separated background track"];
        AppendActivity(L["Loading audio waveforms…"]);
        if (File.Exists(audio)) WaveformPeaks = await workspace.Media.WaveformAsync(audio, 6000, ct);
        var backgrounds = stems.Where(s => ProjectAudioMix.IsBackground(s.Key)).ToArray();
        if (backgrounds.Length == 1)
        {
            BackgroundPeaks = await workspace.Media.WaveformAsync(Path.Combine(session.DirectoryPath, backgrounds[0].Value.Path), 6000, ct);
            BackgroundTrackLabel = L["Separated background"] + " · " + backgrounds[0].Key;
        }
        else if (backgrounds.Length > 1 && session.Project.Source is { } mediaSource)
        {
            string combined = Path.Combine(Path.GetTempPath(), "dubbing-background-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var clips = backgrounds.Select(s => new Dubbing.Core.Media.AudioClip(Path.Combine(session.DirectoryPath, s.Value.Path), TimeSpan.Zero)).ToArray();
                await ProjectAudioMix.RenderAsync(workspace.Media, clips, combined, mediaSource.Duration, mediaSource.SampleRate, ProjectAudioMix.Channels(session.Project), null, ct);
                BackgroundPeaks = await workspace.Media.WaveformAsync(combined, 6000, ct);
                BackgroundTrackLabel = L["Music, ambience and effects"] + " · " + backgrounds.Length;
            }
            finally { File.Delete(combined); }
        }
    }
    private void ScheduleAutoSave()
    {
        if (applyingAutoSave) return;
        autoSave?.Cancel(); autoSave?.Dispose(); autoSave = new();
        _ = AutoSaveAsync(autoSave.Token);
    }
    private async Task AutoSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(800, ct);
            if (IsBusy) return;
            if (pendingSegmentEdits)
            {
                applyingAutoSave = true;
                try { ApplySegmentCore(refresh: false); }
                finally { applyingAutoSave = false; }
            }
            if (session.DirectoryPath is null || !session.IsDirty) return;
            await session.SaveAsync(null, ct); IsDirty = session.IsDirty;
            if (IsBusy) return;
            if (pendingTimingReviews.Count > 0)
                await RunAsync(async token => { await ReviewEditedTimingAsync(token); }, "Review overlaps");
            if (IsPlaybackRunning) await UpdateRealtimeMixAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = L.Format("Operation failed: {0}", ex.Message); }
    }
}
