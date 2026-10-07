using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.App.Services;
using Dubbing.Application.Projects;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Dubbing.App.Localization;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProjectSession session;
    private readonly ProjectFactory factory;
    private readonly IEngineRegistry registry;
    private readonly IProjectDialogs dialogs;
    private readonly TimelineBuilder timeline;
    private readonly TranslationService translator;
    private readonly ILogger<MainViewModel> logger;
    private readonly ImportVideoService importer;
    private readonly GenerateSpeechService speech;
    private readonly ExportVideoService exporter;
    private readonly WorkspaceServices? workspace;
    public TextCatalog L { get; }
    private bool refreshing;
    private CancellationTokenSource? operation;

    [ObservableProperty] private string projectName = "";
    [ObservableProperty] private string sourceLanguage = "en";
    [ObservableProperty] private string targetLanguage = "es";
    [ObservableProperty] private string status = "Ready. Create or open a project.";
    [ObservableProperty] private string location = "Not saved";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool supportsTargetDuration;
    [ObservableProperty] private bool supportsEmotionLabels;

    public ObservableCollection<EngineRowViewModel> Engines { get; } = [];
    public ObservableCollection<SegmentViewModel> Segments { get; } = [];
    public bool CanEdit => !IsBusy;
    public bool CanTranslate => !IsBusy && session.Project.Segments.Any(s => !s.ExcludedFromDubbing && !string.IsNullOrWhiteSpace(s.Source.Text));
    public bool CanGenerateSpeech => !IsBusy && session.Project.Segments.Any(s => !s.ExcludedFromDubbing &&
        !string.IsNullOrWhiteSpace(s.Translation.Text) && s.Translation.Language == session.Project.Settings.TargetLanguage);
    public string WindowTitle => $"{ProjectName}{(IsDirty ? " *" : "")} — OpenDub";

    public MainViewModel(ProjectSession session, ProjectFactory factory, IEngineRegistry registry,
        IProjectDialogs dialogs, TimelineBuilder timeline, TranslationService translator, ILogger<MainViewModel> logger,
        ImportVideoService importer, GenerateSpeechService speech, ExportVideoService exporter,
        WorkspaceServices? workspace = null, TextCatalog? textCatalog = null)
    {
        this.session = session; this.factory = factory; this.registry = registry; this.dialogs = dialogs;
        this.timeline = timeline; this.translator = translator; this.logger = logger;
        this.importer = importer; this.speech = speech; this.exporter = exporter;
        this.workspace = workspace; L = textCatalog ?? new TextCatalog();
        if (workspace is not null) Settings.Load(workspace.Settings.Current);
        Editor.PropertyChanged += (_, args) =>
        {
            if (!loadingSegment && !refreshing && SelectedSegment is not null && args.PropertyName != nameof(Editor.DurationSummary))
            { pendingSegmentEdits = true; IsDirty = true; ScheduleAutoSave(); }
        };
        session.New(CreateDefaultProject(), markDirty: false);
        Refresh();
    }

    partial void OnProjectNameChanged(string value) { Edit(p => p with { Name = value }); OnPropertyChanged(nameof(WindowTitle)); }
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); GenerateCommand.NotifyCanExecuteChanged(); TranslateCommand.NotifyCanExecuteChanged(); }
    partial void OnSourceLanguageChanged(string value) => Edit(p => ProjectEditor.Invalidate(
        p with { Settings = p.Settings with { SourceLanguage = value } }, PipelineStage.Transcription));
    partial void OnTargetLanguageChanged(string value) => Edit(p => ProjectEditor.Invalidate(
        p with { Settings = p.Settings with { TargetLanguage = value } }, PipelineStage.Translation));

    private void Edit(Func<DubbingProject, DubbingProject> update)
    {
        if (refreshing) return;
        DubPreviewPath = null;
        session.Update(update(session.Project)); IsDirty = session.IsDirty;
        GenerateCommand.NotifyCanExecuteChanged(); TranslateCommand.NotifyCanExecuteChanged();
        ScheduleAutoSave();
    }

    [RelayCommand] private async Task NewAsync() => await RunAsync(async ct =>
    {
        if (!await ResolveUnsavedAsync(ct)) return;
        StopPlayback(); session.New(CreateDefaultProject(), markDirty: false); Refresh(); Status = L["Project created."];
    });

    [RelayCommand] private async Task OpenAsync() => await RunAsync(async ct =>
    {
        if (!await ResolveUnsavedAsync(ct)) return;
        var directory = await dialogs.OpenProjectAsync();
        if (directory is null) return;
        StopPlayback(); await session.OpenAsync(directory, ct);
        if (session.Project.Source is { } missing && !File.Exists(missing.Path))
        {
            Status = L["Source video moved. Choose its new location."];
            var replacement = await dialogs.OpenVideoAsync();
            if (replacement is not null)
            {
                if (await Dubbing.Application.Media.ContentFingerprint.ComputeAsync(replacement, ct) != missing.Fingerprint) throw new InvalidOperationException("The selected video does not match this project.");
                session.Update(session.Project with { Source = missing with { Path = replacement } }); await session.SaveAsync(null, ct);
            }
        }
        Refresh(); await LoadWaveformAsync(ct); Status = L["Project opened."];
        if (IsOllamaSelected) await LoadOllamaModelsAsync(ct);
    });

    [RelayCommand] private async Task SaveAsync() => await RunAsync(async ct => { await SaveCoreAsync(ct); });

    private async Task<bool> SaveCoreAsync(CancellationToken ct)
    {
        if (pendingSegmentEdits) ApplySegmentCore();
        var directory = session.DirectoryPath ?? await dialogs.NewProjectDirectoryAsync(ProjectName);
        if (directory is null) return false;
        await session.SaveAsync(directory, ct);
        Location = directory; IsDirty = session.IsDirty; Status = L["Project saved."];
        return true;
    }

    private async Task<bool> ResolveUnsavedAsync(CancellationToken ct)
    {
        if (pendingSegmentEdits) ApplySegmentCore();
        if (!session.IsDirty) return true;
        return await dialogs.ConfirmUnsavedAsync() switch
        {
            UnsavedChoice.Save => await SaveCoreAsync(ct),
            UnsavedChoice.Discard => true,
            _ => false
        };
    }

    public async Task<bool> CanCloseAsync()
    {
        if (IsBusy) { Status = L["Cancel the current operation before closing."]; return false; }
        bool close = false;
        await RunAsync(async ct =>
        {
            if (!await ResolveUnsavedAsync(ct)) return;
            if (workspace is not null) await workspace.Runtimes.ReleaseAsync();
            close = true;
        });
        return close;
    }

    [RelayCommand] private async Task DemoAsync() => await RunAsync(async ct =>
    {
        if (!await ResolveUnsavedAsync(ct)) return;
        var project = factory.Create("mock") with { Name = "OpenDub demo" };
        var audio = new AudioReference("demo.wav");
        var transcript = await registry.GetEngine<ITranscriptionEngine>(project.Engines.Transcription!)
            .TranscribeAsync(new(audio), ReportProgress(), ct);
        var speakers = await registry.GetEngine<IDiarizationEngine>(project.Engines.Diarization!)
            .DiarizeAsync(new(audio), ReportProgress(), ct);
        project = project with
        {
            Segments = timeline.Build(transcript, speakers, project.Settings.TargetLanguage),
            Speakers = speakers.Turns.Select(t => t.SpeakerId).Distinct().Select((id, i) => new SpeakerProfile { Id = id, DisplayName = $"Speaker {i + 1}" }).ToArray()
        };
        project = await translator.TranslateAsync(project, ReportProgress(), ct);
        StopPlayback(); session.New(project); Refresh(); Status = L["Demo ready. Edit the translations and save the project."];
    });

    [RelayCommand(CanExecute = nameof(CanTranslate))] private async Task TranslateAsync() => await RunAsync(async ct =>
    {
        var options = new PipelineDialogOptions
        {
            Translation = true, TargetLanguage = session.Project.Settings.TargetLanguage, Languages = OutputLanguages,
            Engines = registry.GetAvailableEngines(EngineTask.Translation),
            Engine = registry.GetAvailableEngines(EngineTask.Translation).FirstOrDefault(e => e.EngineId == session.Project.Engines.Translation),
            Backend = workspace?.Settings.Current.TranslationBackend ?? "auto",
            ModelPath = workspace?.Settings.Current.TranslationModelPath ?? "",
            OllamaModel = session.Project.Settings.TranslationModelId, SegmentCount = session.Project.Segments.Count,
            ExistingCount = session.Project.Segments.Count(s => !string.IsNullOrWhiteSpace(s.Translation.Text)),
            CountPending = (numbers, overwrite) => session.Project.Segments.Where((s, i) => numbers.Contains(i + 1) && !s.ExcludedFromDubbing && !string.IsNullOrWhiteSpace(s.Source.Text) && (overwrite || string.IsNullOrWhiteSpace(s.Translation.Text))).Count(),
            LoadOllamaModels = async () => await ((Dubbing.Engines.LlamaCpp.OllamaTranslationEngine)registry.GetEngine<ITranslationEngine>(Dubbing.Engines.LlamaCpp.OllamaTranslationEngine.Id)).ListModelsAsync(ct)
        };
        await dialogs.RunPipelineAsync(options, async () =>
        {
            if (options.Engine is null) throw new InvalidOperationException("Select a translation engine.");
            bool changed = session.Project.Settings.TargetLanguage != options.TargetLanguage || session.Project.Engines.Translation != options.Engine.EngineId ||
                session.Project.Settings.TranslationModelId != options.OllamaModel ||
                (options.Engine.ProviderId == "llama.cpp" && options.ModelPath != (workspace?.Settings.Current.TranslationModelPath ?? ""));
            Edit(p =>
            {
                var updated = p with { Settings = p.Settings with { TargetLanguage = options.TargetLanguage, TranslationModelId = options.OllamaModel },
                    Engines = p.Engines with { Translation = options.Engine.EngineId } };
                return changed ? ProjectEditor.Invalidate(updated, PipelineStage.Translation) : updated;
            });
            if (!await SaveCoreAsync(ct)) return;
            using var overrides = workspace?.Settings.OverrideForOperation(workspace.Settings.Current with {
                TranslationBackend = options.Backend, TranslationModelPath = string.IsNullOrWhiteSpace(options.ModelPath) ? null : options.ModelPath });
            try { session.Update(await translator.TranslateAsync(session.Project, ReportProgress(), ct, p => CheckpointTranslationAsync(p, ct), SelectedIds(options), options.OverwriteExisting)); }
            finally { Refresh(); }
            Status = L["Translation complete."];
        }, () => operation?.Cancel());
    });

    private IReadOnlySet<string> SelectedIds(PipelineDialogOptions options) => session.Project.Segments
        .Where((s, i) => (options.AllSegments || options.SelectedNumbers.Contains(i + 1)))
        .Select(s => s.Id).ToHashSet();

    private async Task CheckpointTranslationAsync(DubbingProject project, CancellationToken ct)
    {
        session.Update(project);
        if (session.DirectoryPath is not null) await session.SaveAsync(null, ct);
    }

    [RelayCommand] private async Task ImportVideoAsync() => await RunAsync(async ct =>
    {
        if (!await ResolveUnsavedAsync(ct)) return;
        var input = await dialogs.OpenVideoAsync();
        if (input is null) return;
        async Task ImportChoice(AudioTrackChoice? audioChoice)
        {
            var project = factory.Create() with { Name = Path.GetFileNameWithoutExtension(input),
                Engines = session.Project.Engines, Settings = session.Project.Settings };
            if (audioChoice is not null) project = project with { AudioTracks = audioChoice.Tracks,
                Settings = project.Settings with { AnalysisStreamIndex = audioChoice.AnalysisIndex, UseSeparation = audioChoice.Separate } };
            var directory = await dialogs.NewProjectDirectoryAsync(project.Name);
            if (directory is null) return;
            input = await CopySourceAsync(input, directory, ct);
            StopPlayback(); session.New(project);
            await session.SaveAsync(directory, ct);
            try { await importer.ImportAsync(project, input, directory, ReportProgress(), ct); }
            finally { await session.OpenAsync(directory, CancellationToken.None); Refresh(); }
            await LoadWaveformAsync(ct);
            Status = L["Video imported. Review the segments and generate speech."];
        }
        if (workspace is null) await ImportChoice(null);
        else await ChooseTracksAsync(input, [], null, true, ct, choice => ImportChoice(choice));
    });

    [RelayCommand(CanExecute = nameof(CanGenerateSpeech))] private async Task GenerateAsync() => await RunAsync(async ct =>
    {
        bool HasAudio(DubSegment s) => s.GeneratedSpeech is { } audio && session.DirectoryPath is { } dir && File.Exists(Path.Combine(dir, audio.File));
        var options = new PipelineDialogOptions { SegmentCount = session.Project.Segments.Count,
            ExistingCount = session.Project.Segments.Count(HasAudio),
            CountPending = (numbers, overwrite) => session.Project.Segments.Where((s, i) => numbers.Contains(i + 1) && !s.ExcludedFromDubbing && !string.IsNullOrWhiteSpace(s.Translation.Text) && s.Translation.Language == session.Project.Settings.TargetLanguage && (overwrite || !HasAudio(s))).Count() };
        await dialogs.RunPipelineAsync(options, async () =>
        {
            if (!await SaveCoreAsync(ct)) return;
            string directory = session.DirectoryPath!;
            var affectedIds = SelectedIds(options).Where(id => session.Project.Segments.Any(s => s.Id == id && !s.ExcludedFromDubbing &&
                !string.IsNullOrWhiteSpace(s.Translation.Text) && s.Translation.Language == session.Project.Settings.TargetLanguage && (options.OverwriteExisting || !HasAudio(s)))).ToHashSet();
            try { await speech.GenerateAsync(session.Project, directory, ReportProgress(), ct, null, options.FitDuration, affectedIds, options.OverwriteExisting); }
            finally { await session.OpenAsync(directory, CancellationToken.None); Refresh(); }
            Status = L["Speech generated. Review the timing before exporting."];
            await ResolveAffectedCollisionsAsync(ct, affectedIds);
        }, () => operation?.Cancel());
    });

    [RelayCommand] private async Task ExportAsync() => await RunAsync(ExportWithDialogAsync);
    [RelayCommand] private void Cancel() => operation?.Cancel();

    private IProgress<EngineProgress> ReportProgress() => new Progress<EngineProgress>(p =>
    {
        Progress = p.Fraction * 100;
        string line = $"{L[p.Message]} · {Progress:F0}%" + (p.SegmentId is null ? "" : $" · {p.SegmentId}");
        if (line != lastProgressLine) { lastProgressLine = line; Status = line; }
    });

    private async Task RunAsync(Func<CancellationToken, Task> action, [System.Runtime.CompilerServices.CallerMemberName] string operationName = "")
    {
        if (IsBusy) return;
        StopAutoSave();
        IsBusy = true; Progress = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        AppendActivity(L.Format("Starting {0}", operationName.Replace("Async", "")));
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        bool failed = false;
        try { if (pendingSegmentEdits) ApplySegmentCore(); await action(cancellation.Token); }
        catch (OperationCanceledException) { failed = true; Status = L["Operation cancelled."]; }
        catch (Exception ex)
        {
            failed = true;
            logger.LogError(ex, "Project operation failed for {ProjectId}", session.Project.Id);
            Status = L.Format("Operation failed: {0}", L[ex.Message]);
        }
        finally
        {
            try { if (failed && workspace is not null) await workspace.Runtimes.ReleaseAsync(); }
            catch (Exception ex) { AppendActivity("Model release failed: " + ex.Message); }
            operation = null; IsBusy = false; IsDirty = session.IsDirty;
            AppendActivity(L.Format("Finished {0} · {1:F1} s", operationName.Replace("Async", ""), watch.Elapsed.TotalSeconds));
        }
    }

    private void Refresh()
    {
        DubPreviewPath = null;
        GenerateCommand.NotifyCanExecuteChanged(); TranslateCommand.NotifyCanExecuteChanged();
        string? selectedId = SelectedSegment?.Id;
        refreshing = true;
        try
        {
            var project = session.Project;
            ProjectName = project.Name; SourceLanguage = project.Settings.SourceLanguage; TargetLanguage = project.Settings.TargetLanguage;
            Location = session.DirectoryPath ?? L["Not saved"]; IsDirty = session.IsDirty;
            Engines.Clear();
            AddEngine(EngineTask.Transcription, L["Transcription"], project.Engines.Transcription);
            AddEngine(EngineTask.Diarization, L["Diarization"], project.Engines.Diarization);
            AddEngine(EngineTask.Alignment, L["Alignment"], project.Engines.Alignment);
            AddEngine(EngineTask.Translation, L["Translation"], project.Engines.Translation);
            AddEngine(EngineTask.Tts, L["Tts"], project.Engines.Tts);
            AddEngine(EngineTask.Separation, L["Separation"], project.Engines.Separation);
            Segments.Clear();
            foreach (var segment in project.Segments)
            {
                var row = new SegmentViewModel(segment, L, project.Speakers.FirstOrDefault(s => s.Id == segment.SpeakerId)?.DisplayName) { Number = Segments.Count + 1 };
                row.TranslationChanged += item =>
                {
                    if (refreshing) return;
                    Edit(p => ProjectEditor.EditTranslation(p, item.Id, item.TranslationText));
                    if (SelectedSegment?.Id == item.Id) { loadingSegment = true; Editor.TranslationText = item.TranslationText; loadingSegment = false; }
                };
                row.SourceChanged += item =>
                {
                    if (refreshing) return;
                    Edit(p => p with { Segments = p.Segments.Select(s => s.Id != item.Id ? s : s with {
                        Source = s.Source with { Text = item.SourceText },
                        DirtyStages = s.DirtyStages.Union(s.Translation.Locked ? StageDependencies.After(PipelineStage.Translation) : new[] { PipelineStage.Translation }.Concat(StageDependencies.After(PipelineStage.Translation))).ToArray()
                    }).ToArray() });
                    if (SelectedSegment?.Id == item.Id) { loadingSegment = true; Editor.SourceText = item.SourceText; loadingSegment = false; }
                };
                Segments.Add(row);
            }
            SelectedSegment = Segments.FirstOrDefault(s => s.Id == selectedId);
            RefreshCapabilities();
            RefreshEditor();
            RefreshAudioControls();
            NormalizeSegments = project.Settings.NormalizeSegments; ParagraphSegments = project.Settings.ParagraphSegments;
            SelectedOllamaModel = project.Settings.TranslationModelId;
            UseSourceVoicePerSegment = project.Settings.UseSourceVoicePerSegment;
            OnPropertyChanged(nameof(IsOllamaSelected));
        }
        finally { refreshing = false; }
    }

    private void AddEngine(EngineTask task, string label, string? id)
    {
        var options = registry.GetAvailableEngines(task);
        var row = new EngineRowViewModel(task, label, options, id);
        row.Changed += item =>
        {
            var selected = item.Selected?.EngineId;
            Edit(p => p with { Engines = item.Task switch
            {
                EngineTask.Transcription => p.Engines with { Transcription = selected },
                EngineTask.Diarization => p.Engines with { Diarization = selected },
                EngineTask.Alignment => p.Engines with { Alignment = selected },
                EngineTask.Translation => p.Engines with { Translation = selected },
                EngineTask.Tts => p.Engines with { Tts = selected },
                EngineTask.Separation => p.Engines with { Separation = selected },
                _ => p.Engines
            }});
            Edit(p => ProjectEditor.Invalidate(p, item.Task switch
            {
                EngineTask.Transcription => PipelineStage.Transcription,
                EngineTask.Diarization => PipelineStage.Diarization,
                EngineTask.Alignment => PipelineStage.Alignment,
                EngineTask.Translation => PipelineStage.Translation,
                EngineTask.Separation => PipelineStage.Separation,
                _ => PipelineStage.Tts
            }));
            RefreshCapabilities();
            if (item.Task == EngineTask.Translation)
            {
                OnPropertyChanged(nameof(IsOllamaSelected));
                if (IsOllamaSelected) RefreshOllamaModelsCommand.Execute(null);
            }
        };
        Engines.Add(row);
    }

    private void RefreshCapabilities()
    {
        var capabilities = Engines.FirstOrDefault(x => x.Task == EngineTask.Tts)?.Selected?.Capabilities;
        SupportsTargetDuration = capabilities?.SupportsTargetDuration == true;
        SupportsEmotionLabels = capabilities?.SupportsEmotionLabels == true;
    }
}
