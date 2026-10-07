using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dubbing.App;
using Dubbing.App.Controls;
using Dubbing.App.Localization;
using Dubbing.App.Services;
using Dubbing.App.ViewModels;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Integration.Tests;

public sealed class OverlapEditorTests
{
    [AvaloniaFact]
    public async Task ClosingDuringRegenerationCancelsWorkAndReleasesModalOwner()
    {
        string root = Path.Combine(Path.GetTempPath(), "overlap-cancel-" + Guid.NewGuid());
        var owner = new Window(); owner.Show(); var entered = new TaskCompletionSource();
        var saved = new DubbingProject { Segments = [new() { End = TimeSpan.FromSeconds(3), Translation = new("es", "Hola") },
            new() { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4) }] };
        async Task<DubbingProject> Commit(DubbingProject p, string? id, CancellationToken ct)
        { saved = p; entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return saved; }
        using var model = new OverlapEditorModel(saved, SegmentCollisions.Find(saved)!, root, new EditorMedia(), Commit, () => saved);
        try
        {
            var pending = new ProjectDialogs(owner).EditOverlapAsync(model, default);
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == "Regenerate and fit")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); dialog.Close();
            Assert.Equal(CollisionChoice.Cancel, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Choice);
            Assert.Empty(owner.OwnedWindows); Assert.True(owner.IsEnabled);
        }
        finally { owner.Close(); model.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [RuntimeFact]
    public async Task DialogAudioPlaysVirtualTimelineAndSupportsPausedSeekAndResume()
    {
        string root = Path.Combine(Path.GetTempPath(), "overlap-audio-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "voice.wav");
        await new Dubbing.Engines.Mock.MockTtsEngine().SynthesizeAsync(new() { Text = "test", Language = "es", OutputPath = path, TargetDuration = TimeSpan.FromSeconds(4) }, null, default);
        var type = typeof(MainWindow).Assembly.GetType("Dubbing.App.Services.OverlapAudioPlayer")!;
        var audio = Activator.CreateInstance(type, nonPublic: true)!;
        object? Call(string method, params object[] args) => type.GetMethod(method)!.Invoke(audio, args);
        double Position() => (double)type.GetProperty("Position")!.GetValue(audio)!;
        async Task Until(Func<bool> predicate)
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!predicate()) await Task.Delay(20, timeout.Token); }
        try
        {
            var plan = new Dubbing.Application.Playback.RealtimePlaybackPlan("", TimeSpan.FromSeconds(12), [new(path, TimeSpan.FromSeconds(5))]);
            await (Task)Call("PlayAsync", plan, 5.1, 8.8, CancellationToken.None)!;
            await Until(() => Position() > 5.3);
            var player = (LibVLCSharp.Shared.MediaPlayer)type.GetField("player", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(audio)!;
            Assert.False(player.Mute); Assert.Equal(100, player.Volume); Assert.True(player.AudioTrack >= 0);
            Call("Pause"); await Until(() => player.State == LibVLCSharp.Shared.VLCState.Paused);
            Call("Seek", 6.2); await Until(() => Position() >= 6.1);
            Call("Resume"); await Until(() => Position() > 6.5);
            Call("Stop"); Assert.False(player.IsPlaying);
        }
        finally { ((IDisposable)audio).Dispose(); Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task VisualEditorDragsBoundsRegeneratesWithoutClosingAndMergesNeighbours()
    {
        string root = Path.Combine(Path.GetTempPath(), "overlap-ui-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var owner = new Window(); owner.Show();
        var media = new EditorMedia();
        var a = new DubSegment { Id = "a", Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(5), Source = new("en", "Stay with me."), Translation = new("es", "Quédate conmigo."), GeneratedSpeech = new("a.wav", TimeSpan.FromSeconds(3), "a"), DirtyStages = [] };
        var b = new DubSegment { Id = "b", Start = TimeSpan.FromSeconds(6), End = TimeSpan.FromSeconds(10), Source = new("en", "We need to leave before the storm reaches us."), Translation = new("es", "Tenemos que irnos antes de que nos alcance la tormenta."), GeneratedSpeech = new("b.wav", TimeSpan.FromSeconds(5), "b"), DirtyStages = [] };
        var c = new DubSegment { Id = "c", Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(13), Source = new("en", "I know a safe place."), Translation = new("es", "Conozco un lugar seguro."), GeneratedSpeech = new("c.wav", TimeSpan.FromSeconds(3), "c"), DirtyStages = [] };
        foreach (string file in new[] { "original.wav", "a.wav", "b.wav", "c.wav" }) await File.WriteAllTextAsync(Path.Combine(root, file), "fixture");
        var saved = new DubbingProject { Source = new(Path.Combine(root, "original.wav"), "source", TimeSpan.FromSeconds(20), 1, 24000), Stems = new Dictionary<string, AudioReference> { ["vocals"] = new("original.wav") }, Segments = [a, b, c] };
        int generations = 0;
        async Task<DubbingProject> Commit(DubbingProject draft, string? id, CancellationToken ct)
        {
            saved = draft;
            if (id is not null)
            {
                generations++; await Task.Delay(30, ct);
                saved = saved with { Segments = saved.Segments.Select(s => s.Id == id ? s with {
                    GeneratedSpeech = new("b.wav", s.TargetDuration, "new"), FittedSpeech = null, DirtyStages = [] } : s).ToArray() };
            }
            return saved;
        }
        using var model = new OverlapEditorModel(saved, SegmentCollisions.Find(saved)!, root, media, Commit, () => saved);
        Task<CollisionDecision>? pending = null;
        try
        {
            pending = new ProjectDialogs(owner).EditOverlapAsync(model, default);
            var dialog = owner.OwnedWindows.OfType<OverlapEditorWindow>().Single();
            async Task Ready()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                do { await Task.Delay(20, timeout.Token); dialog.UpdateLayout(); }
                while (!dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "Regenerate and fit").IsEffectivelyEnabled);
            }
            void Click(string tag) => dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == tag)
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Ready();
            Assert.Equal(3, model.Context.Count); Assert.Single(model.OriginalWaves); Assert.Equal(3, model.DubWaves.Count);
            Assert.True(model.WindowStart < a.Start.TotalSeconds); Assert.True(model.WindowEnd > c.End.TotalSeconds);
            var wave = dialog.GetVisualDescendants().OfType<OverlapWaveform>().First();
            var edge = wave.TranslatePoint(new Point((b.End.TotalSeconds - wave.WindowStart) / (wave.WindowEnd - wave.WindowStart) * wave.Bounds.Width, 80), dialog)!.Value;
            dialog.MouseMove(edge); Assert.Equal(StandardCursorType.SizeWestEast.ToString(), wave.Cursor!.ToString());
            dialog.MouseDown(edge, MouseButton.Left); dialog.MouseMove(edge.WithX(edge.X - 20), RawInputModifiers.LeftMouseButton); dialog.MouseUp(edge.WithX(edge.X - 20), MouseButton.Left);
            await Ready(); Assert.True(model.Selected.End < b.End); Assert.Same(b, saved.Segments[1]);
            var translated = dialog.GetVisualDescendants().OfType<TextBox>().Single(x => x.Tag as string == "DubText:b");
            translated.Text = "Vámonos antes de la tormenta.";
            if (Environment.GetEnvironmentVariable("OPENDUB_OVERLAP_SCREENSHOT") is { Length: > 0 } screenshot)
            { dialog.UpdateLayout(); using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(screenshot); }
            Click("Regenerate and fit"); await Ready();
            Assert.True(dialog.IsVisible); Assert.False(pending.IsCompleted); Assert.Equal(1, generations);
            Assert.Equal("Vámonos antes de la tormenta.", saved.Segments[1].Translation.Text);
            Assert.Null(model.CurrentCollision);
            Click("Merge next & regenerate"); await Ready();
            Assert.Equal(2, generations); Assert.Equal(2, saved.Segments.Count);
            Assert.Contains("Conozco un lugar seguro.", model.Selected.Translation.Text);
            Click("Done"); Assert.Equal(CollisionChoice.Edited, (await pending).Choice);
        }
        finally
        {
            foreach (var child in owner.OwnedWindows.ToArray()) child.Close(); if (pending is not null) await pending;
            owner.Close(); model.Dispose(); Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task TranslationDialogEditsAndResetsPromptBeforeStarting()
    {
        var owner = new Window(); owner.Show();
        var options = new PipelineDialogOptions { Translation = true, SegmentCount = 1, Languages = ["es"],
            Engines = [new Dubbing.Engines.Mock.MockTranslationEngine().Descriptor], Engine = new Dubbing.Engines.Mock.MockTranslationEngine().Descriptor };
        string? sent = null;
        try
        {
            var pending = new ProjectDialogs(owner).RunPipelineAsync(options, () => { sent = options.TranslationPrompt; return Task.CompletedTask; }, () => { });
            var dialog = owner.OwnedWindows.Single(); dialog.UpdateLayout();
            var prompt = dialog.GetVisualDescendants().OfType<TextBox>().Single(b => b.Tag as string == "TranslationPrompt");
            prompt.Text = "Use Spanish from Spain.";
            void Click(string tag) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == tag).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Click("Reset prompt"); Assert.Equal(TranslationPrompts.Default, prompt.Text);
            prompt.Text += " Use Spanish from Spain."; Click("Start"); await pending;
            Assert.Contains("Spanish from Spain", sent);
        }
        finally { owner.Close(); }
    }

    private sealed class EditorMedia : IMediaEngine
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(new MediaInfo(TimeSpan.FromSeconds(20), []));
        public Task CutAsync(string input, string output, TimeSpan start, TimeSpan duration, CancellationToken ct) => File.WriteAllTextAsync(output, "fixture", ct);
        public Task<IReadOnlyList<float>> WaveformAsync(string audio, int peakCount, CancellationToken ct) => Task.FromResult<IReadOnlyList<float>>(
            Enumerable.Range(0, peakCount).Select(i => (float)(Math.Abs(Math.Sin(i * .19)) * Math.Abs(Math.Sin(i * .027)) * .7 + .03)).ToArray());
        public Task ExtractAudioAsync(string i, string o, int r, int c, IProgress<EngineProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task ExtractTrackAsync(string i, int n, string o, int r, int c, IProgress<EngineProgress>? p, CancellationToken ct, TimeSpan? start = null, TimeSpan? duration = null) => throw new NotSupportedException();
        public Task MixAsync(MixRequest r, IProgress<EngineProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task MuxAsync(string v, string a, string o, IProgress<EngineProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task StretchAsync(string i, string o, double rate, CancellationToken ct) => throw new NotSupportedException();
        public Task ExtractFrameAsync(string v, string o, TimeSpan t, CancellationToken ct) => throw new NotSupportedException();
    }
}
