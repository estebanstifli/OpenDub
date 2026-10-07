using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Dubbing.App;
using Dubbing.App.Services;
using Dubbing.App.ViewModels;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;
using Dubbing.Infrastructure.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Dubbing.Application.Engines;
using Dubbing.Infrastructure.Processes;
using Dubbing.Media.FFmpeg;

[assembly: AvaloniaTestApplication(typeof(Dubbing.Integration.Tests.TestAppBuilder))]

namespace Dubbing.Integration.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Dubbing.App.App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class ShellTests
{
    [AvaloniaFact]
    public async Task FirstRunAssistantCanBeCancelledBeforeDownloading()
    {
        string root = Path.Combine(Path.GetTempPath(), "opendub-setup-" + Guid.NewGuid());
        var owner = new Window();
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        var session = new ProjectSession(store);
        using var coordinator = new RuntimeCoordinator();
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance); var locator = new RuntimeLocator();
        var media = new FFmpegMediaEngine(runner, locator, new());
        var settings = new Dubbing.Infrastructure.Configuration.SettingsStore(Path.Combine(root, "settings.json"));
        var catalog = new Dubbing.Infrastructure.Models.ModelCatalog(Path.Combine(AppContext.BaseDirectory, "engine-catalog"));
        using var models = new Dubbing.Infrastructure.Models.VerifiedModelManager(catalog, settings, runner);
        var workspace = new WorkspaceServices(media, settings, catalog, models, locator, runner, coordinator);
        var vm = new MainViewModel(session, new ProjectFactory(registry), registry, new ProjectDialogs(owner),
            new TimelineBuilder(), new TranslationService(registry), NullLogger<MainViewModel>.Instance,
            new ImportVideoService(media, store, registry, new TimelineBuilder()), new GenerateSpeechService(new EngineSelectionService(registry), store),
            new ExportVideoService(media, new DubRenderService(media), store), workspace);
        owner.DataContext = vm; owner.Show();
        try
        {
            var pending = vm.SetupCommand.ExecuteAsync(null);
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            Assert.Equal("Prepare OpenDub", dialog.Title);
            if (Environment.GetEnvironmentVariable("OPENDUB_SETUP_SCREENSHOT") is { Length: > 0 } screenshot)
            { using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            dialog.Close(); await pending;
            Assert.False(File.Exists(settings.FilePath)); Assert.False(vm.IsBusy);
        }
        finally { vm.StopAutoSave(); owner.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact] public async Task OperationSettingsRestoreWithoutChangingSavedDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "operation-settings-" + Guid.NewGuid());
        var settings = new Dubbing.Infrastructure.Configuration.SettingsStore(Path.Combine(root, "settings.json"));
        try
        {
            await settings.SaveAsync(new() { TranslationBackend = "auto", TranslationModelPath = "default.gguf" }, default);
            string saved = await File.ReadAllTextAsync(settings.FilePath);
            try
            {
                using var scope = settings.OverrideForOperation(settings.Current with { TranslationBackend = "cpu", TranslationModelPath = "override.gguf" });
                Assert.Equal("cpu", settings.Current.TranslationBackend);
                Assert.Equal("override.gguf", settings.Current.TranslationModelPath);
                throw new OperationCanceledException();
            }
            catch (OperationCanceledException) { }
            Assert.Equal("auto", settings.Current.TranslationBackend); Assert.Equal(Dubbing.Infrastructure.Configuration.AppPaths.Resolve("default.gguf"), settings.Current.TranslationModelPath);
            Assert.Equal(saved, await File.ReadAllTextAsync(settings.FilePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(true, "complete")]
    [InlineData(true, "background")]
    [InlineData(true, "cancel")]
    [InlineData(false, "complete")]
    [InlineData(false, "cancel")]
    public async Task PipelineDialogsStartCancelAndBackground(bool translate, string action)
    {
        var owner = new Window { DataContext = new { Status = "Working", Progress = 40.0, ActivityText = "Loading model..." } }; owner.Show();
        var completion = new TaskCompletionSource();
        int starts = 0, cancels = 0;
        var engine = new Dubbing.Engines.Mock.MockTranslationEngine().Descriptor;
        var options = new PipelineDialogOptions { Translation = translate, Languages = ["es", "en"], Engines = [engine], Engine = engine, SegmentCount = 2 };
        try
        {
            var pending = new ProjectDialogs(owner).RunPipelineAsync(options, () => { starts++; return completion.Task; }, () => { cancels++; completion.TrySetCanceled(); });
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            void Click(string tag) => dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, tag)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, starts);
            if (Environment.GetEnvironmentVariable("DUBBING_PIPELINE_SCREENSHOT") is { Length: > 0 } screenshot && translate && action == "complete")
            { using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            Click("Start"); Assert.Equal(1, starts); Assert.False(pending.IsCompleted);
            if (action == "background")
            {
                Click("Continue in background");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {}, Avalonia.Threading.DispatcherPriority.Background);
                Assert.Empty(owner.OwnedWindows); Assert.False(pending.IsCompleted);
                completion.SetResult(); await pending; Assert.Equal(0, cancels);
            }
            else if (action == "cancel")
            { Click("Cancel"); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.True(cancels > 0); }
            else { completion.SetResult(); await pending; Assert.Equal(0, cancels); }
            Assert.Empty(owner.OwnedWindows);
        }
        finally { completion.TrySetResult(); owner.Close(); }
    }

    [Theory]
    [InlineData("3,5,9", "3,5,9")]
    [InlineData("3-5, 9,4", "3,4,5,9")]
    public void SegmentRangesParse(string input, string expected)
    {
        var options = new PipelineDialogOptions { SegmentCount = 10, AllSegments = false, SegmentRanges = input };
        Assert.Equal(expected, string.Join(",", options.ParseSelection().Order()));
    }
    [Theory]
    [InlineData("0")][InlineData("11")][InlineData("5-3")][InlineData("")][InlineData("1,,2")]
    public void InvalidSegmentRangesRejected(string input) => Assert.Throws<FormatException>(() =>
        new PipelineDialogOptions { SegmentCount = 10, AllSegments = false, SegmentRanges = input }.ParseSelection());

    [AvaloniaFact]
    public async Task PipelineFailureRemainsVisibleUntilClosed()
    {
        var owner = new Window(); owner.Show();
        try
        {
            var pending = new ProjectDialogs(owner).RunPipelineAsync(new PipelineDialogOptions { SegmentCount = 2 },
                () => Task.FromException(new InvalidOperationException("Test failure")), () => { });
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "Start")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsVisible); Assert.False(pending.IsCompleted);
            dialog.Close(); await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact] public async Task SegmentDetailEditsCopyAndCancelsWithoutChangingOriginal()
    {
        var owner = new Window(); owner.Show();
        try
        {
            var original = new SegmentEditorViewModel { StartSeconds = 2, EndSeconds = 4, SourceText = "Original", TranslationText = "Translation" };
            var editor = new SegmentEditorViewModel(); editor.CopyFrom(original);
            var voice = new Dubbing.Infrastructure.Models.ModelCatalog(Path.Combine(AppContext.BaseDirectory, "engine-catalog")).Models.Single(m => m.Family == "index_tts2").Descriptor;
            editor.VoiceEngine = voice; editor.EmotionMode = Dubbing.Core.Projects.EmotionMode.SourceAudio;
            var model = new SegmentDetailViewModel("Segment 2 selected", editor, [], [voice],
                Enumerable.Range(0, 600).Select(i => (float)Math.Abs(Math.Sin(i*.14))*.8f).ToArray(), 0, 8, 8, (_,_) => Task.CompletedTask, () => {});
            var pending = new ProjectDialogs(owner).EditSegmentAsync(model);
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            var wave = dialog.GetVisualDescendants().OfType<Dubbing.App.Controls.ReferenceWaveform>().Single();
            var point = wave.TranslatePoint(new Point(wave.Bounds.Width*.25, 50), dialog)!.Value;
            dialog.MouseDown(point, MouseButton.Left); dialog.MouseMove(point.WithX(point.X-20), RawInputModifiers.LeftMouseButton);
            dialog.MouseUp(point.WithX(point.X-20), MouseButton.Left);
            Assert.True(editor.StartSeconds < 2); Assert.Equal(2, original.StartSeconds);
            if (Environment.GetEnvironmentVariable("DUBBING_DETAIL_SCREENSHOT") is { Length: > 0 } screenshot)
            { using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            dialog.Close(false); Assert.False(await pending); Assert.Equal("Original", original.SourceText);
            Assert.Empty(owner.OwnedWindows);
            pending = new ProjectDialogs(owner).EditSegmentAsync(model);
            dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            var apply = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Apply segment"));
            editor.EndSeconds = 9;
            apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.False(pending.IsCompleted);
            editor.EndSeconds = 5;
            apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.True(await pending);
            Assert.Equal(5, model.Editor.EndSeconds); Assert.Equal(4, original.EndSeconds);

        }
        finally { owner.Close(); }
    }

    [AvaloniaFact] public async Task ReferenceEditorValidatesRangeAndReturnsAdjustedTimes()
    {
        var owner = new Window(); owner.Show();
        try
        {
            var pending = new ProjectDialogs(owner).EditVoiceReferenceAsync([.1f,.5f,.9f,.3f], 28, 35, 31.04, 32,
                (_, _) => Task.CompletedTask, () => { });
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            var apply = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Use reference"));
            Assert.False(apply.IsEnabled);
            var inputs = dialog.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
            inputs[0].Value = 30.90m; inputs[1].Value = 32.10m;
            Assert.True(apply.IsEnabled);
            dialog.UpdateLayout();
            if (Environment.GetEnvironmentVariable("DUBBING_REFERENCE_SCREENSHOT") is { Length: > 0 } screenshot)
            { using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var result = await pending;
            Assert.NotNull(result); Assert.Equal(30.9, result.Value.Start); Assert.Equal(32.1, result.Value.End);
            Assert.Empty(owner.OwnedWindows);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact] public async Task ProjectFoldersUseConfiguredRootAndSafeUniqueNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "projects-setting-" + Guid.NewGuid());
        var settings = new Dubbing.Infrastructure.Configuration.SettingsStore(Path.Combine(root, "settings.json"));
        try
        {
            Assert.Equal(Dubbing.Infrastructure.Configuration.AppPaths.ProjectsRoot, settings.Current.EffectiveProjectsDirectory);
            await settings.SaveAsync(new() { ProjectsDirectory = Path.Combine(root, "jobs") }, default);
            var dialogs = new ProjectDialogs(new Window(), settings: settings);
            var first = await dialogs.NewProjectDirectoryAsync("THE BATMAN – tráiler");
            var second = await dialogs.NewProjectDirectoryAsync("THE BATMAN – tráiler");
            Assert.True(Directory.Exists(settings.Current.EffectiveProjectsDirectory));
            Assert.Equal(settings.Current.EffectiveProjectsDirectory, Path.GetDirectoryName(first));
            Assert.All(Path.GetFileName(first)!, c => Assert.True(char.IsAscii(c)));
            Assert.NotEqual(first, second);
            Assert.Equal(settings.Current.ProjectsDirectory, new Dubbing.Infrastructure.Configuration.SettingsStore(settings.FilePath).Current.ProjectsDirectory);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData("complete")]
    [InlineData("background")]
    [InlineData("cancel")]
    public async Task ImportDialogTracksOperationLifetime(string action)
    {
        var owner = new Window { DataContext = new { Status = "Separating audio…", Progress = 42.0, ActivityText = "Preparing audio\nSeparating voices and music…" } }; owner.Show();
        var completion = new TaskCompletionSource();
        int cancellations = 0;
        try
        {
            var pending = new ProjectDialogs(owner).ChooseAudioTracksAsync(
                [new(0, "aac", 2, 48000, "en", "Dialogue", Role: AudioTrackRole.Mixed)], 0, true,
                (_, _) => Task.CompletedTask, () => { }, _ => completion.Task,
                () => { cancellations++; completion.TrySetCanceled(); });
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            void Click(string label) => dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Click("Start import"); dialog.UpdateLayout();
            Assert.False(pending.IsCompleted); Assert.True(dialog.IsVisible);
            Assert.Equal(42, dialog.GetVisualDescendants().OfType<ProgressBar>().Single().Value);
            if (action == "background")
            {
                Click("Continue in background");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                Assert.Empty(owner.OwnedWindows); Assert.False(pending.IsCompleted);
                completion.SetResult(); Assert.NotNull(await pending); Assert.Equal(0, cancellations);
            }
            else if (action == "cancel")
            {
                Click("Cancel"); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.True(cancellations > 0);
            }
            else
            {
                if (Environment.GetEnvironmentVariable("DUBBING_IMPORT_SCREENSHOT") is { Length: > 0 } screenshot)
                { using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
                completion.SetResult(); Assert.NotNull(await pending);
            }
            Assert.Empty(owner.OwnedWindows);
        }
        finally { completion.TrySetResult(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task DialogContinuationRunsAfterOwnerIsDetached()
    {
        var owner = new Window(); owner.Show();
        try
        {
            var dialogs = new ProjectDialogs(owner);
            bool closeReturned = false;
            Window first = null!;
            async Task FollowUp()
            {
                Assert.True(await dialogs.ConfirmAsync("Confirm"));
                Assert.True(closeReturned);
                Assert.Empty(owner.OwnedWindows);
                Assert.Null(first.Owner);
            }
            var pending = FollowUp();
            first = owner.OwnedWindows.Single();
            first.Close(true);
            closeReturned = true;
            await pending;
            var cancelled = dialogs.ConfirmUnsavedAsync();
            owner.OwnedWindows.Single().Close();
            Assert.Equal(UnsavedChoice.Cancel, await cancelled);
            Assert.Empty(owner.OwnedWindows);
        }
        finally { owner.Close(); }
    }
    [AvaloniaFact]
    public async Task AudioTrackDialogChoosesRolesAndCancelsWithoutChangingProject()
    {
        var owner = new Window { Width = 1000, Height = 750 }; owner.Show();
        try
        {
            int stops = 0;
            var dialogs = new ProjectDialogs(owner);
            SourceAudioTrack[] tracks = [new(1, "aac", 2, 48000, "fra", "French full mix", Role: AudioTrackRole.Mixed),
                new(2, "aac", 2, 48000, "eng", "English dialogue"), new(3, "aac", 6, 48000, "und", "Music and effects")];
            var pending = dialogs.ChooseAudioTracksAsync(tracks, 1, true, (_, _) => Task.CompletedTask, () => stops++);
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            var selectors = dialog.GetVisualDescendants().OfType<ComboBox>().ToArray();
            selectors[0].SelectedIndex = 1;
            selectors[2].SelectedIndex = (int)AudioTrackRole.Dialogue;
            selectors[3].SelectedIndex = (int)AudioTrackRole.Background;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            dialog.UpdateLayout();
            if (Environment.GetEnvironmentVariable("DUBBING_TRACKS_SCREENSHOT") is { Length: > 0 } screenshot)
            { using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Use these tracks"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var result = await pending;
            Assert.NotNull(result); Assert.Equal(2, result.AnalysisIndex);
            Assert.Equal(new[] { AudioTrackRole.Ignore, AudioTrackRole.Dialogue, AudioTrackRole.Background }, result.Tracks.Select(t => t.Role));
            Assert.Equal(AudioTrackRole.Mixed, tracks[0].Role); Assert.Equal(1, stops);
            var cancelled = dialogs.ChooseAudioTracksAsync(tracks, 1, true, (_, _) => Task.CompletedTask, () => stops++);
            owner.OwnedWindows.Single().Close(); Assert.Null(await cancelled); Assert.Equal(2, stops);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task SuccessfulUiCommandsRetainModelUntilExplicitUnload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dubbing-ui-residency-" + Guid.NewGuid());
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        var session = new ProjectSession(store);
        using var coordinator = new RuntimeCoordinator();
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var locator = new RuntimeLocator();
        var media = new FFmpegMediaEngine(runner, locator, new());
        var settings = new Dubbing.Infrastructure.Configuration.SettingsStore(Path.Combine(directory, "settings.json"));
        var workspace = new WorkspaceServices(media, settings, null!, null!, locator, runner, coordinator);
        var vm = new MainViewModel(session, new ProjectFactory(registry), registry, new FakeDialogs(directory),
            new TimelineBuilder(), new TranslationService(registry), NullLogger<MainViewModel>.Instance,
            new ImportVideoService(media, store, registry, new TimelineBuilder()), new GenerateSpeechService(new EngineSelectionService(registry), store),
            new ExportVideoService(media, new DubRenderService(media), store), workspace);
        int releases = 0;
        try
        {
            await coordinator.RunAsync("audio", () => releases++, () => Task.FromResult(true), default);
            await vm.DemoCommand.ExecuteAsync(null);
            await vm.SaveCommand.ExecuteAsync(null);
            vm.SelectedSegment = vm.Segments[0];
            vm.Editor.SourceText = "Automatically saved source";
            vm.Editor.TranslationText = "Guardado automáticamente";
            await Task.Delay(1200);
            var saved = await store.LoadAsync(directory, default);
            Assert.Equal("Guardado automáticamente", saved.Segments[0].Translation.Text);
            Assert.Equal("Automatically saved source", vm.Segments[0].SourceText);
            Assert.False(vm.IsDirty);
            double validEnd = vm.Editor.EndSeconds;
            vm.Editor.EndSeconds = -1;
            await Task.Delay(1000);
            Assert.Equal(validEnd, (await store.LoadAsync(directory, default)).Segments[0].End.TotalSeconds);
            vm.Editor.EndSeconds = validEnd;
            await Task.Delay(1000);
            Assert.Equal(0, releases);
            await coordinator.RunAsync("audio", () => releases++, () => Task.FromResult(true), default);
            Assert.Equal(0, releases);
            await vm.UnloadModelsCommand.ExecuteAsync(null);
            Assert.Equal(1, releases);
            await coordinator.RunAsync("audio", () => releases++, () => Task.FromResult(true), default);
            Assert.True(await vm.CanCloseAsync());
            Assert.Equal(2, releases);
        }
        finally { vm.StopAutoSave(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task ShellCreatesEditsSavesAndReopensDemoWithoutModels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dubbing-shell-" + Guid.NewGuid());
        var dialogs = new FakeDialogs(directory);
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        var session = new ProjectSession(store);
        using var coordinator = new RuntimeCoordinator();
        using var ollama = new Dubbing.Engines.LlamaCpp.OllamaTranslationEngine(coordinator, new OllamaTests.Handler());
        var registry = new EngineRegistry(MockEngineCatalog.Create().Append(ollama));
        var media = new FFmpegMediaEngine(new ProcessRunner(NullLogger<ProcessRunner>.Instance), new RuntimeLocator(), new());
        var vm = new MainViewModel(session, new ProjectFactory(registry), registry, dialogs,
            new TimelineBuilder(), new TranslationService(registry), NullLogger<MainViewModel>.Instance,
            new ImportVideoService(media, store, registry, new TimelineBuilder()), new GenerateSpeechService(new EngineSelectionService(registry), store),
            new ExportVideoService(media, new DubRenderService(media), store));
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Assert.Contains("OpenDub", window.Title);
            var navigation = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("nav"));
            Assert.Equal(4, navigation.Items.Count);
            vm.SidebarNavigationIndex = 3; Assert.Equal(5, vm.NavigationIndex);
            vm.NavigationIndex = 3; Assert.Equal(3, vm.SidebarNavigationIndex);
            vm.SidebarNavigationIndex = 0; Assert.Equal(0, vm.NavigationIndex);
            Assert.Equal(new[] { "zh", "en", "ja", "es", "ar" }, vm.OutputLanguages);
            Assert.False(vm.GenerateCommand.CanExecute(null)); Assert.False(vm.TranslateCommand.CanExecute(null));
            Assert.Equal(6, vm.Engines.Count);
            await vm.DemoCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Segments.Count); Assert.True(vm.TranslateCommand.CanExecute(null));
            Assert.True(vm.GenerateCommand.CanExecute(null));
            vm.SelectedSpeaker = vm.Speakers.First();
            Assert.Equal(session.Project.Segments.Count(s => s.SpeakerId == vm.SelectedSpeaker.Id), vm.SpeakerSegments.Count);
            vm.Segments[0].TranslationText = "Cambio manual";
            Assert.True(vm.IsDirty);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.False(vm.IsDirty);
            Assert.True(vm.GenerateCommand.CanExecute(null));
            var translation = vm.Engines.Single(e => e.Task == Dubbing.Core.Engines.EngineTask.Translation);
            translation.Selected = translation.Options.Single(e => e.EngineId == Dubbing.Engines.LlamaCpp.OllamaTranslationEngine.Id);
            if (vm.RefreshOllamaModelsCommand.ExecutionTask is { } refresh) await refresh;
            Assert.True(vm.IsOllamaSelected);
            Assert.Equal(new[] { "test:8b" }, vm.OllamaModels);
            vm.SelectedOllamaModel = "test:8b";
            vm.UseSourceVoicePerSegment = false;
            await vm.SaveCommand.ExecuteAsync(null);
            await vm.NewCommand.ExecuteAsync(null);
            Assert.Empty(vm.Segments);
            await vm.OpenCommand.ExecuteAsync(null);
            Assert.True(vm.IsOllamaSelected);
            Assert.Equal("test:8b", vm.SelectedOllamaModel);
            Assert.Equal("test:8b", session.Project.Settings.TranslationModelId);
            Assert.False(vm.UseSourceVoicePerSegment);
            window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 3;
            window.UpdateLayout();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), box => ReferenceEquals(box.ItemsSource, vm.OllamaModels));
            if (Environment.GetEnvironmentVariable("DUBBING_ENGINES_SCREENSHOT") is { Length: > 0 } enginesScreenshot)
            {
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame); frame.Save(enginesScreenshot);
            }
            if (Environment.GetEnvironmentVariable("DUBBING_MIXER_SCREENSHOT") is { Length: > 0 } mixerScreenshot)
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.ListenBackgroundCommand));
                button.GetVisualAncestors().OfType<ScrollViewer>().First().ScrollToEnd();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(mixerScreenshot);
            }
            if (Environment.GetEnvironmentVariable("DUBBING_SPEAKERS_SCREENSHOT") is { Length: > 0 } speakerScreenshot)
            {
                window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
                window.UpdateLayout();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(speakerScreenshot);
            }
            window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
            window.UpdateLayout();
            vm.SelectedSegment = vm.Segments[0];
            vm.Segments[0].SourceText = "Updated original text.";
            vm.Segments[0].TranslationText = "Cambio manual";
            Assert.Equal("Updated original text.", vm.Editor.SourceText);
            Assert.Equal("Cambio manual", vm.Editor.TranslationText);
            Assert.True(vm.Segments[0].IsActive);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal("Updated original text.", (await store.LoadAsync(directory, default)).Segments[0].Source.Text);
            if (Environment.GetEnvironmentVariable("DUBBING_SEGMENTS_SCREENSHOT") is { Length: > 0 } segmentsScreenshot)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(segmentsScreenshot);
            }
            window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 0;
            window.UpdateLayout();
            double sidebar = vm.SidebarWidth; vm.ToggleSidebarCommand.Execute(null); Assert.True(vm.SidebarWidth < sidebar);
            vm.ToggleSidebarCommand.Execute(null);
            vm.ZoomInCommand.Execute(null); Assert.True(vm.TimelineWidth > vm.TimelineViewportWidth);
            vm.FitTimelineCommand.Execute(null); Assert.Equal(vm.TimelineViewportWidth, vm.TimelineWidth);
            Assert.Contains("Project opened", vm.ActivityText);
            ActivityJournal.Write("Runtime diagnostic fixture");
            Assert.Contains("Runtime diagnostic fixture", vm.ActivityText);
            int seeks = 0; vm.SeekRequested += _ => seeks++;
            vm.UpdatePlaybackPosition(4.6);
            Assert.Equal(vm.Segments[1].Id, vm.SelectedSegment!.Id);
            Assert.Equal("Segment 2 selected", vm.SelectedSegmentTitle);
            Assert.Equal(vm.Segments[1].SourceText, vm.Editor.SourceText);
            Assert.Equal(4.6, vm.Playhead); Assert.Equal(0, seeks);
            int pauses = 0; vm.PauseRequested += () => pauses++;
            vm.OriginalVoiceMuted = false; vm.DubMuted = true; vm.OriginalVoiceGain = .7;
            Assert.Equal(3, pauses);
            Assert.Equal(4.6, vm.Playhead);
            Assert.False(session.Project.Settings.OriginalVoiceMuted);
            Assert.True(session.Project.Settings.DubMuted);
            Assert.Equal(.7, session.Project.Settings.OriginalVoiceGain);
            Assert.Null(vm.DubPreviewPath);
            vm.UpdatePlaybackPosition(.5);
            Assert.Equal(vm.Segments[0].Id, vm.SelectedSegment!.Id);
            Assert.Equal(0, seeks);
            vm.Seek(4.7); Assert.Equal(1, seeks); Assert.Equal(4.7, vm.Playhead);
            var activity = window.FindControl<TextBox>("ActivityBox")!;
            var splitter = window.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.ResizeDirection == GridResizeDirection.Rows);
            double activityHeight = activity.Bounds.Height;
            var grip = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(grip.WithY(grip.Y - 60), RawInputModifiers.LeftMouseButton);
            window.MouseUp(grip.WithY(grip.Y - 60), MouseButton.Left);
            window.UpdateLayout();
            Assert.True(activity.Bounds.Height > activityHeight + 40);
            window.MouseDown(grip.WithY(grip.Y - 60), MouseButton.Left);
            window.MouseMove(grip, RawInputModifiers.LeftMouseButton);
            window.MouseUp(grip, MouseButton.Left);
            window.UpdateLayout();
            Assert.Equal("Cambio manual", vm.Segments[0].TranslationText);
            Assert.True(session.Project.Segments[0].Translation.Locked);
            Assert.True(vm.SupportsTargetDuration);
            Assert.False(vm.SupportsEmotionLabels);
            vm.SelectedSegment = vm.Segments[1];
            string selectedId = vm.SelectedSegment.Id;
            vm.Editor.TranslationText = "Edited second segment";
            vm.Editor.ExcludedFromDubbing = true;
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(selectedId, vm.SelectedSegment!.Id);
            Assert.Equal("Edited second segment", (await store.LoadAsync(directory, default)).Segments[1].Translation.Text);
            Assert.True(session.Project.Segments[1].Translation.Locked);
            Assert.True((await store.LoadAsync(directory, default)).Segments[1].ExcludedFromDubbing);
            vm.Editor.ExcludedFromDubbing = false;
            await vm.ApplySegmentCommand.ExecuteAsync(null);
            Assert.False(session.Project.Segments[1].ExcludedFromDubbing);
            if (Environment.GetEnvironmentVariable("DUBBING_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(screenshot);
            }
            Assert.True(await vm.CanCloseAsync());
            if (Environment.GetEnvironmentVariable("DUBBING_COMPACT_SCREENSHOT") is { Length: > 0 } compactScreenshot)
            {
                window.Width = 1100; window.Height = 820; vm.SidebarExpanded = false; window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame); frame.Save(compactScreenshot);
            }
        }
        finally
        {
            window.DataContext = null;
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task CancelingUnsavedDialogPreservesProject()
    {
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        var dialogs = new FakeDialogs("unused") { Choice = UnsavedChoice.Cancel };
        var session = new ProjectSession(new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance));
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        var media = new FFmpegMediaEngine(new ProcessRunner(NullLogger<ProcessRunner>.Instance), new RuntimeLocator(), new());
        var vm = new MainViewModel(session, new ProjectFactory(registry), registry, dialogs,
            new TimelineBuilder(), new TranslationService(registry), NullLogger<MainViewModel>.Instance,
            new ImportVideoService(media, store, registry, new TimelineBuilder()), new GenerateSpeechService(new EngineSelectionService(registry), store),
            new ExportVideoService(media, new DubRenderService(media), store));
        vm.ProjectName = "No perder";
        await vm.NewCommand.ExecuteAsync(null);
        Assert.Equal("No perder", vm.ProjectName);
        Assert.False(await vm.CanCloseAsync());
    }

    private sealed class FakeDialogs(string directory) : IProjectDialogs
    {
        public UnsavedChoice Choice { get; set; } = UnsavedChoice.Discard;
        public Task<UnsavedChoice> ConfirmUnsavedAsync() => Task.FromResult(Choice);
        public Task<string?> NewProjectDirectoryAsync(string name) => Task.FromResult<string?>(directory);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(directory);
        public Task<string?> OpenVideoAsync() => Task.FromResult<string?>(null);
    }
}

