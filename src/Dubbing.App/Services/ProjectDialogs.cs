using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dubbing.App.Localization;
using Dubbing.Core.Projects;

namespace Dubbing.App.Services;

public enum UnsavedChoice { Save, Discard, Cancel }
public sealed record AudioTrackChoice(IReadOnlyList<SourceAudioTrack> Tracks, int AnalysisIndex, bool Separate);
public interface IProjectDialogs
{
    Task<(double Start, double End)?> EditVoiceReferenceAsync(IReadOnlyList<float> peaks, double windowStart, double windowEnd,
        double start, double end, Func<double, double, Task> preview, Action stop) => Task.FromResult<(double, double)?>(null);
    Task<bool> EditSegmentAsync(Dubbing.App.ViewModels.SegmentDetailViewModel model) => Task.FromResult(false);
    Task RunPipelineAsync(PipelineDialogOptions options, Func<Task> start, Action cancel) => start();
    Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    Task<string?> OpenProjectAsync();
    Task<string?> OpenVideoAsync();
    Task<string?> NewProjectDirectoryAsync(string name);
    Task<UnsavedChoice> ConfirmUnsavedAsync();
    Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    Task<bool> ConfirmAsync(string message) => Task.FromResult(false);
    async Task<AudioTrackChoice?> ChooseAudioTracksAsync(IReadOnlyList<SourceAudioTrack> tracks, int selected, bool separate,
        Func<int, double, Task> preview, Action stop, Func<AudioTrackChoice, Task>? process = null, Action? cancelOperation = null) {
        var choice = new AudioTrackChoice(tracks, selected, separate);
        if (process is not null) await process(choice);
        return choice;
    }
}

public sealed partial class ProjectDialogs(Window owner, TextCatalog? text = null, Dubbing.Infrastructure.Configuration.SettingsStore? settings = null) : IProjectDialogs
{
    public Task<bool> EditSegmentAsync(Dubbing.App.ViewModels.SegmentDetailViewModel model) => ShowDialogAsync<bool>(new Dubbing.App.SegmentDetailWindow(model, text));
    private async Task<T> ShowDialogAsync<T>(Window dialog)
    {
        try { return await dialog.ShowDialog<T>(owner); }
        finally
        {
            // Avalonia completes ShowDialog during Closed, before detaching Owner.
            // Let native destruction finish before a continuation opens a Windows picker.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
    public async Task<AudioTrackChoice?> ChooseAudioTracksAsync(IReadOnlyList<SourceAudioTrack> tracks, int selected, bool separate,
        Func<int, double, Task> preview, Action stop, Func<AudioTrackChoice, Task>? process = null, Action? cancelOperation = null)
    {
        var dialog = new Window { Title = text["Import and prepare audio"], Width = 920, Height = 720, MinWidth = 700, MinHeight = 500,
            Background = Avalonia.Media.Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Task? running = null; bool background = false; AudioTrackChoice? startedChoice = null;
        dialog.Closing += (_, _) => { if (running is { IsCompleted: false } && !background) cancelOperation?.Invoke(); };
        var panel = new StackPanel { Spacing = 12, Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = text["Choose the dialogue source. Other language mixes must be ignored, not used as background."], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var choices = tracks.Select(t => new TrackLabel(t.Index, $"#{t.Index} · {t.Language ?? "und"} · {t.Title ?? ""} · {t.Codec} · {t.ChannelLayout ?? t.Channels.ToString()} · {t.SampleRate} Hz")).ToArray();
        panel.Children.Add(new TextBlock { Text = text["1. Choose the audio track"], FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var analysis = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(t => t.Index == selected), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (tracks.Count > 1) panel.Children.Add(analysis);
        else panel.Children.Add(new TextBlock { Text = text["One audio track detected; it is selected automatically."], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var sample = new NumericUpDown { Minimum = 0, Maximum = 86400, Value = 0, Increment = 5, Width = 160 };
        panel.Children.Add(new TextBlock { Text = text["Preview start (seconds). Preview audio plays up to 15 seconds; it does not trim the import."] }); sample.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(sample);
        var roleOptions = Enum.GetValues<AudioTrackRole>().Select(r => new RoleLabel(r, text[r.ToString()])).ToArray();
        var selectors = new Dictionary<int, ComboBox>();
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        foreach (var track in tracks)
        {
            var row = new StackPanel { Spacing = 5 };
            row.Children.Add(new TextBlock { Text = choices.Single(t => t.Index == track.Index).Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var roles = new ComboBox { ItemsSource = roleOptions, SelectedItem = roleOptions.Single(r => r.Role == track.Role), Width = 310 };
            selectors[track.Index] = roles;
            var listen = new Button { Content = text["Preview audio"] };
            listen.Click += async (_, _) =>
            {
                listen.IsEnabled = false;
                try { await preview(track.Index, (double)(sample.Value ?? 0)); error.Text = ""; }
                catch (Exception ex) { error.Text = ex.Message; }
                finally { listen.IsEnabled = true; }
            };
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { roles, listen } });
            panel.Children.Add(row);
        }
        analysis.SelectionChanged += (_, _) =>
        {
            if (analysis.SelectedItem is not TrackLabel chosen) return;
            foreach (var (index, selector) in selectors)
            {
                var current = ((RoleLabel)selector.SelectedItem!).Role;
                if (index == chosen.Index && current is not AudioTrackRole.Dialogue)
                    selector.SelectedItem = roleOptions.Single(r => r.Role == AudioTrackRole.Mixed);
                else if (index != chosen.Index && current is AudioTrackRole.Dialogue or AudioTrackRole.Mixed)
                    selector.SelectedItem = roleOptions.Single(r => r.Role == AudioTrackRole.Ignore);
            }
        };
        panel.Children.Add(new TextBlock { Text = text["2. Voice separation"], FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var separation = new CheckBox { Content = text["Separate dialogue from mixed audio"], IsChecked = separate };
        panel.Children.Add(separation);
        panel.Children.Add(new TextBlock { Text = text["AI separation may leave voice residue or alter effects. Surround separation uses stereo. Assign clean music/effects tracks when available."], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(error);
        var accept = new Button { Content = text[process is null ? "Use these tracks" : "Start import"] }; var cancel = new Button { Content = text["Cancel"] }; var stopButton = new Button { Content = text["Stop"] };
        var backgroundButton = new Button { Content = text["Continue in background"], IsVisible = false };
        backgroundButton.Click += (_, _) => { background = true; dialog.Close(startedChoice); };
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12 };
        progressBar.Bind(ProgressBar.ValueProperty, new Avalonia.Data.Binding("Progress") { Source = owner.DataContext });
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Status") { Source = owner.DataContext });
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 420, FontFamily = new Avalonia.Media.FontFamily("Consolas"), FontSize = 11 };
        log.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("ActivityText") { Source = owner.DataContext });
        log.TextChanged += (_, _) => log.CaretIndex = log.Text?.Length ?? 0;
        var processing = new StackPanel { IsVisible = false, Spacing = 16, Margin = new(20), Children = { status, progressBar, log } };
        stopButton.Click += (_, _) => stop(); cancel.Click += (_, _) => { if (running is { IsCompleted: false }) cancelOperation?.Invoke(); dialog.Close(); };
        accept.Click += async (_, _) =>
        {
            if (analysis.SelectedItem is not TrackLabel chosen) return;
            var configured = tracks.Select(t => t with { Role = ((RoleLabel)selectors[t.Index].SelectedItem!).Role }).ToArray();
            if (configured.Count(t => t.Role is AudioTrackRole.Mixed or AudioTrackRole.Dialogue) != 1 ||
                configured.Single(t => t.Index == chosen.Index).Role is not (AudioTrackRole.Mixed or AudioTrackRole.Dialogue))
            { error.Text = text["Choose one Mixed or Dialogue track for analysis; ignore other language mixes."]; return; }
            var choice = new AudioTrackChoice(configured, chosen.Index, separation.IsChecked == true);
            if (process is null) { dialog.Close(choice); return; }
            startedChoice = choice; stop(); panel.IsVisible = false; processing.IsVisible = true;
            accept.IsVisible = false; stopButton.IsVisible = false; backgroundButton.IsVisible = true;
            try { running = process(choice); await running; }
            catch (Exception) { /* The outer awaiting operation reports the failure. */ }
            finally { if (dialog.IsVisible) dialog.Close(choice); }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(20), Children = { accept, stopButton, backgroundButton, cancel } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = new StackPanel { Children = { panel, processing } } }); Grid.SetRow(buttons, 1); grid.Children.Add(buttons); dialog.Content = grid;
        try
        {
            var result = await ShowDialogAsync<AudioTrackChoice?>(dialog);
            if (running is not null) await running;
            return result;
        }
        finally { stop(); }
    }
    private sealed record TrackLabel(int Index, string Label) { public override string ToString() => Label; }
    private sealed record RoleLabel(AudioTrackRole Role, string Label) { public override string ToString() => Label; }
    private readonly TextCatalog text = text ?? new TextCatalog();
    public async Task<string?> PickFileAsync(string title)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = this.text[title], AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new Window { Title = text["Confirm"], Width = 480, Height = 190, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var yes = new Button { Content = text["Confirm"] }; var no = new Button { Content = text["Cancel"] };
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new(24), Spacing = 20, Children = { new TextBlock { Text = text[message], TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } } } };
        return await ShowDialogAsync<bool>(dialog);
    }
    public async Task<string?> OpenVideoAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = text["Import video"], AllowMultiple = false,
            FileTypeFilter = [new("Video") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.webm"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await (owner.OwnedWindows.LastOrDefault(w => w.IsVisible) ?? owner).StorageProvider.OpenFolderPickerAsync(new() { Title = text["Choose folder"], AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> OpenProjectAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = text["Open project.json"], AllowMultiple = false,
            FileTypeFilter = [new("Dubbing project") { Patterns = ["project.json", "*.json"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        return path is null ? null : Path.GetDirectoryName(path);
    }

    public Task<string?> NewProjectDirectoryAsync(string name)
    {
        var parent = (settings?.Current ?? new Dubbing.Infrastructure.Configuration.AppSettings()).EffectiveProjectsDirectory;
        Directory.CreateDirectory(parent);
        var safeName = ProjectFolderName.Normalize(name);
        // Unique folder: Save As never replaces an unrelated project.
        return Task.FromResult<string?>(Path.Combine(parent, $"{safeName}-{Guid.NewGuid().ToString("N")[..8]}.dubproj"));
    }

    public async Task<UnsavedChoice> ConfirmUnsavedAsync()
    {
        var dialog = new Window { Title = text["Save changes"], Width = 450, Height = 180,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result) in new[] { (text["Save"], UnsavedChoice.Save), (text["Discard"], UnsavedChoice.Discard), (text["Cancel"], UnsavedChoice.Cancel) })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => dialog.Close(result);
            buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new(24), Spacing = 24, Children =
            { new TextBlock { Text = text["Do you want to save changes to this project?"] }, buttons } };
        // Closing the dialog via the window decoration must mean Cancel.
        var resultChoice = await ShowDialogAsync<UnsavedChoice?>(dialog);
        return resultChoice ?? UnsavedChoice.Cancel;
    }
}
