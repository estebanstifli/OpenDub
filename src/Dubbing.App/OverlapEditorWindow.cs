using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dubbing.App.Controls;
using Dubbing.App.Localization;
using Dubbing.App.Services;
using Dubbing.App.ViewModels;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;

namespace Dubbing.App;

public sealed class OverlapEditorWindow : Window
{
    private readonly OverlapEditorModel model;
    private readonly TextCatalog text;
    private readonly OverlapWaveform originalWave = new() { Height = 100, ClipToBounds = true };
    private readonly OverlapWaveform dubWave = new() { Height = 100, ClipToBounds = true, IsDub = true };
    private readonly Grid originalTexts = new() { ColumnSpacing = 10 }, dubTexts = new() { ColumnSpacing = 10 };
    private readonly NumericUpDown from = new() { FormatString = "F3", Increment = .01m, Minimum = 0, Width = 160 };
    private readonly NumericUpDown to = new() { FormatString = "F3", Increment = .01m, Minimum = 0, Width = 160 };
    private readonly TextBlock title = new() { FontSize = 23, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#FFCC66") };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock clock = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
    private readonly StackPanel content = new() { Spacing = 7 }, actions = new() { Spacing = 8 };
    private readonly StackPanel runningPanel = new() { Spacing = 5, IsVisible = false };
    private readonly OverlapAudioPlayer player = new();
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration cancellationRegistration;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private CancellationTokenSource? operation;
    private Task work = Task.CompletedTask;
    private TextBox? originalEdit, translationEdit;
    private Button done = null!, previous = null!, next = null!, trim = null!, allow = null!, cancel = null!;
    private bool loading, busy, closed, currentDub;
    private double playhead;

    public OverlapEditorWindow(OverlapEditorModel model, TextCatalog text, CancellationToken ct, object? activitySource = null)
    {
        this.model = model; this.text = text; lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Title = text["Resolve overlap"]; Width = 1240; Height = 960; MinWidth = 880; MinHeight = 640;
        Background = Brush.Parse("#0F1A2B"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        cancellationRegistration = ct.Register(() => Dispatcher.UIThread.Post(() => { if (!closed) Close(new CollisionDecision(CollisionChoice.Cancel)); }));
        content.Children.Add(title); content.Children.Add(summary);
        content.Children.Add(Label("Gold: selected segment. Blue: neighbours. Red: audio beyond its time slot. Drag the gold edges or click another segment."));
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        transport.Children.Add(Button("Pause", "pause", () => { player.Pause(); return Task.CompletedTask; }));
        transport.Children.Add(Button("Stop", "square", () => { player.Stop(); Seek(model.WindowStart); return Task.CompletedTask; }));
        transport.Children.Add(clock); content.Children.Add(transport);
        content.Children.Add(TrackHeader("Original voice", () => Play(false)));
        content.Children.Add(originalWave); content.Children.Add(originalTexts);
        content.Children.Add(TrackHeader("Dubbed voice", () => Play(true)));
        content.Children.Add(dubWave); content.Children.Add(dubTexts);
        var timing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        timing.Children.Add(Label("Start (seconds)")); timing.Children.Add(from); timing.Children.Add(Label("End (seconds)")); timing.Children.Add(to);
        content.Children.Add(timing);
        content.Children.Add(Label("Timing changes do not stretch the WAV. Edit the translation and choose Regenerate and fit to create new audio. Save changes keeps this editor open."));
        from.ValueChanged += (_, _) => ApplyDraft(); to.ValueChanged += (_, _) => ApplyDraft();
        foreach (var wave in new[] { originalWave, dubWave })
        {
            wave.RangeChanged += (a, b) =>
            {
                player.Pause(); loading = true; from.Value = (decimal)a; to.Value = (decimal)b; loading = false; ApplyDraft();
            };
            wave.RangeCompleted += () => _ = Run(RefreshWaves);
            wave.SegmentSelected += id =>
            {
                if (id == model.SelectedId || busy) return;
                if (!ApplyDraft()) return;
                model.SelectedId = id; _ = Run(RefreshWaves);
            };
            wave.SeekRequested += Seek;
        }
        var editButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        editButtons.Children.Add(Button("Save changes", "save", () => Run(async token => { RequireDraft(); await model.SaveAsync(false, token); await RefreshWaves(token); })));
        editButtons.Children.Add(Button("Regenerate and fit", "refresh-cw", () => Run(async token => { RequireDraft(); await model.SaveAsync(true, token); await RefreshWaves(token); }), primary: true));
        previous = Button("Merge previous & regenerate", "scissors", () => Merge(true)); editButtons.Children.Add(previous);
        next = Button("Merge next & regenerate", "scissors", () => Merge(false)); editButtons.Children.Add(next);
        actions.Children.Add(editButtons);
        var resolveButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        trim = Button("Trim dub at boundary", "scissors", () => Resolve(CollisionChoice.Limit)); resolveButtons.Children.Add(trim);
        allow = Button("Keep overlap", "audio-lines", () => Resolve(CollisionChoice.Allow)); resolveButtons.Children.Add(allow);
        var progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, IsIndeterminate = activitySource is null };
        if (activitySource is not null) progress.Bind(ProgressBar.ValueProperty, new Binding("Progress") { Source = activitySource });
        runningPanel.Children.Add(progress);
        if (activitySource is not null)
        {
            var activity = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 40 };
            activity.Bind(TextBlock.TextProperty, new Binding("Status") { Source = activitySource }); runningPanel.Children.Add(activity);
        }
        var footer = new StackPanel { Spacing = 8 }; footer.Children.Add(status); footer.Children.Add(runningPanel); footer.Children.Add(actions);
        done = Button("Done", "save", () => Run(async token =>
        {
            RequireDraft(); if (model.HasDraft) await model.SaveAsync(false, token);
            if (model.CurrentCollision is null) Close(new CollisionDecision(CollisionChoice.Edited));
            else UpdateSummary();
        }), primary: true);
        cancel = Button("Close", "x", () =>
        {
            if (busy) operation?.Cancel();
            else Close(new CollisionDecision(CollisionChoice.Cancel));
            return Task.CompletedTask;
        });
        resolveButtons.Children.Add(done); resolveButtons.Children.Add(cancel); footer.Children.Add(resolveButtons);
        footer.Children.Add(Label("Save, regeneration and merge are saved immediately. Closing discards only unsaved edits. Trimming may cut speech; merging uses the first speaker."));
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new(20), RowSpacing = 10 };
        layout.Children.Add(new ScrollViewer { Content = content }); Grid.SetRow(footer, 1); layout.Children.Add(footer); Content = layout;
        timer.Tick += (_, _) =>
        {
            if (!player.IsPlaying) return;
            if (player.Position >= model.WindowEnd) { player.Pause(); Seek(model.WindowEnd); }
            else SetPlayhead(player.Position);
        };
        Opened += (_, _) => { timer.Start(); _ = Run(RefreshWaves); };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); timer.Stop(); };
        RefreshFields(); SetPlayhead(model.Selected.Start.TotalSeconds);
    }
    private TextBlock Label(string key) => new() { Text = text[key], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightSteelBlue };
    private Button Button(string label, string icon, Func<Task> action, bool primary = false)
    {
        var button = new Button { Tag = label, Margin = new(0, 0, 8, 5), CornerRadius = new(6), Background = Brush.Parse(primary ? "#2876F4" : "#17263A"),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { new LucideIcon { Kind = icon }, new TextBlock { Text = text[label] } } } };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { if (!closed) status.Text = text[ex.Message]; } }; return button;
    }
    private Control TrackHeader(string label, Func<Task> play)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        row.Children.Add(new TextBlock { Text = text[label], FontWeight = FontWeight.SemiBold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Button(label == "Original voice" ? "Play original" : "Play dub", "play", play)); return row;
    }
    private Task Merge(bool previous) => Run(async token => { RequireDraft(); await model.MergeAsync(previous, token); await RefreshWaves(token); });
    private Task Resolve(CollisionChoice choice) => Run(async token => { RequireDraft(); await model.ResolveAsync(choice, token); await RefreshWaves(token); });
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (busy || closed) return;
        busy = true; player.Stop(); content.IsEnabled = false; actions.IsEnabled = false; done.IsEnabled = false;
        trim.IsEnabled = false; allow.IsEnabled = false;
        runningPanel.IsVisible = true; status.Text = text["Working…"];
        if (cancel.Content is StackPanel p && p.Children.LastOrDefault() is TextBlock label) label.Text = text["Cancel operation"];
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        async Task Execute()
        {
            try { await action(operation.Token); if (!closed) status.Text = model.HasDraft ? text["Unsaved changes"] : text["Ready."]; }
            catch (OperationCanceledException) { player.Stop(); if (!closed) status.Text = text["Operation cancelled. Completed changes remain saved."]; }
            catch (Exception ex) { player.Stop(); if (!closed) status.Text = text[ex.Message]; }
            finally
            {
                operation.Dispose(); operation = null; busy = false;
                if (!closed)
                {
                    content.IsEnabled = true; actions.IsEnabled = true; runningPanel.IsVisible = false; RefreshFields();
                    if (cancel.Content is StackPanel p && p.Children.LastOrDefault() is TextBlock label) label.Text = text["Close"];
                }
            }
        }
        work = Execute(); await work;
    }
    private async Task RefreshWaves(CancellationToken ct) { await model.LoadWavesAsync(ct); if (!closed) RefreshFields(); }
    private void RefreshFields()
    {
        loading = true;
        try
        {
            title.Text = text.Format("Segment {0} selected", model.Number(model.Selected));
            from.Maximum = to.Maximum = (decimal)(model.Project.Source?.Duration.TotalSeconds ?? model.WindowEnd);
            from.Value = (decimal)model.Selected.Start.TotalSeconds; to.Value = (decimal)model.Selected.End.TotalSeconds;
            PopulateTexts(originalTexts, false); PopulateTexts(dubTexts, true); RefreshRegions(); UpdateSummary();
        }
        finally { loading = false; }
    }
    private void PopulateTexts(Grid grid, bool dub)
    {
        grid.Children.Clear(); var context = model.Context; grid.ColumnDefinitions = new ColumnDefinitions(string.Join(',', context.Select(_ => "*")));
        for (int i = 0; i < context.Count; i++)
        {
            var segment = context[i]; bool selected = segment.Id == model.SelectedId;
            var box = new TextBox { Text = dub ? segment.Translation.Text : segment.Source.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 58, MaxHeight = 80, IsReadOnly = !selected, BorderBrush = Brush.Parse(selected ? "#FFCC66" : "#365579"), Tag = dub ? "DubText:" + segment.Id : "OriginalText:" + segment.Id };
            if (selected) { if (dub) translationEdit = box; else originalEdit = box; box.TextChanged += (_, _) => ApplyDraft(); }
            var column = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = $"#{model.Number(segment)} · {segment.Start:mm\\:ss\\.fff} → {segment.End:mm\\:ss\\.fff}", Foreground = Brush.Parse(selected ? "#FFCC66" : "#8DB2DD") }, box } };
            Grid.SetColumn(column, i); grid.Children.Add(column);
        }
    }
    private bool ApplyDraft()
    {
        if (loading) return true;
        try
        {
            model.Edit((double)(from.Value ?? 0), (double)(to.Value ?? 0), originalEdit?.Text ?? "", translationEdit?.Text ?? "");
            RefreshRegions(); UpdateSummary(); status.Text = model.HasDraft ? text["Unsaved changes"] : ""; return true;
        }
        catch (Exception ex) { status.Text = text[ex.Message]; return false; }
    }
    private void RequireDraft() { if (!ApplyDraft()) throw new InvalidOperationException(status.Text); }
    private void RefreshRegions()
    {
        var regions = model.Context.Select(s => new OverlapRegion(s.Id, model.Number(s), s.Start.TotalSeconds, s.End.TotalSeconds,
            s.Start.TotalSeconds + ProjectAudioMix.EffectiveDuration(s).TotalSeconds)).ToArray();
        foreach (var wave in new[] { originalWave, dubWave })
        {
            wave.Regions = regions; wave.SelectedId = model.SelectedId; wave.WindowStart = model.WindowStart; wave.WindowEnd = model.WindowEnd;
            wave.Waves = wave.IsDub ? model.DubWaves : model.OriginalWaves; wave.Playhead = playhead; wave.InvalidateVisual();
        }
    }
    private void UpdateSummary()
    {
        var collision = model.CurrentCollision;
        summary.Text = collision is null ? text["No unresolved overlap for this segment."] : text.Format("Segment {0} overlaps segment {1} by {2:F1} ms.",
            collision.FirstNumber, collision.SecondNumber, (collision.AudibleEnd - collision.Second.Start).TotalMilliseconds);
        if (collision is not null && collision.AudibleEnd > collision.First.End)
            summary.Text += " " + text.Format("Generated audio ends at {0:F3} s; the segment ends at {1:F3} s.", collision.AudibleEnd.TotalSeconds, collision.First.End.TotalSeconds);
        var ordered = model.Ordered; previous.IsEnabled = ordered.First().Id != model.SelectedId; next.IsEnabled = ordered.Last().Id != model.SelectedId;
        trim.IsEnabled = collision is not null && collision.Second.Start > collision.First.Start; allow.IsEnabled = collision is not null;
        done.IsEnabled = !busy && collision is null;
    }
    private void SetPlayhead(double seconds)
    {
        playhead = Math.Clamp(seconds, model.WindowStart, model.WindowEnd); clock.Text = TimeSpan.FromSeconds(playhead).ToString(@"hh\:mm\:ss\.fff");
        originalWave.Playhead = dubWave.Playhead = playhead; originalWave.InvalidateVisual(); dubWave.InvalidateVisual();
    }
    private void Seek(double seconds) { SetPlayhead(seconds); if (player.IsPlaying || player.IsPaused) player.Seek(playhead); }
    private async Task Play(bool dub)
    {
        if (busy || !ApplyDraft()) return;
        if (player.IsPaused && currentDub == dub && !model.HasDraft) { player.Resume(); return; }
        await Run(async token =>
        {
            await RefreshWaves(token); var plan = await model.PlaybackAsync(dub, token);
            currentDub = dub;
            double start = playhead >= model.WindowEnd - .02 ? model.WindowStart : Math.Clamp(playhead, model.WindowStart, model.WindowEnd);
            await player.PlayAsync(plan, start, model.WindowEnd, token);
        });
    }
    public async Task FinishAsync()
    {
        lifetime.Cancel(); try { await work; } finally { timer.Stop(); player.Dispose(); operation?.Dispose(); cancellationRegistration.Dispose(); lifetime.Dispose(); }
    }
}
