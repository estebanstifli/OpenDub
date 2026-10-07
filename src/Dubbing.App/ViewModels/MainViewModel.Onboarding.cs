using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.App.Services;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Infrastructure.Configuration;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string setupSummary = "";
    private DubbingProject CreateDefaultProject()
    {
        var p = factory.Create();
        return p with { Settings = p.Settings with { TargetLanguage = workspace?.Settings.Current.DefaultTargetLanguage ?? "es" } };
    }
    public async Task InitializeWorkspaceAsync()
    {
        if (workspace is null) return;
        await RefreshModelsCommand.ExecuteAsync(null);
        SetupSummary = L.Format("Models installed: {0}/{1}", Models.Count(m => m.Model.Installed), Models.Count);
        if (!workspace.Settings.Current.SetupCompleted || Models.Any(m => !m.Model.Installed)) await SetupCommand.ExecuteAsync(null);
    }
    private TextBlock Label(string value) => new() { Text = L[value], TextWrapping = TextWrapping.Wrap };
    private void FolderField(StackPanel panel, string title, string value, Action<string> changed)
    {
        panel.Children.Add(Label(title));
        var input = new TextBox { Text = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        input.TextChanged += (_, _) => changed(input.Text ?? "");
        var browse = new Button { Content = L["Browse"] };
        browse.Click += async (_, _) => { var path = await dialogs.PickFolderAsync(); if (path is not null) input.Text = path; };
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; row.Children.Add(input); Grid.SetColumn(browse, 1); row.Children.Add(browse); panel.Children.Add(row);
    }
    [RelayCommand] private async Task SetupAsync() => await RunAsync(async ct =>
    {
        if (workspace is null) return;
        string folder = Settings.ModelDirectory;
        double size = workspace.Catalog.Models.SelectMany(m => m.Files).DistinctBy(f => f.Path).Sum(f => f.Size) / 1073741824d;
        var options = new PipelineDialogOptions { OperationTitle = "Prepare OpenDub", ConfigureFields = panel =>
        {
            panel.Children.Add(Label("Welcome to OpenDub 0.1.0"));
            panel.Children.Add(Label("Prepare the local AI models once. Your videos are processed on this computer. Downloads can be cancelled and resumed."));
            panel.Children.Add(Label(L.Format("Recommended models: {0} · Total size: {1:F1} GiB", workspace.Catalog.Models.Count, size)));
            foreach (var model in workspace.Catalog.Models) panel.Children.Add(Label($"{model.DisplayName} · {model.License}"));
            FolderField(panel, "Model directory", folder, value => folder = value);
            panel.Children.Add(Label(AppPaths.IsPortable ? "Portable mode: settings and models travel with the data folder." : "Installed mode: data is stored in your user folder."));
        } };
        await dialogs.RunPipelineAsync(options, async () =>
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose a model folder.");
            folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
            await workspace.Settings.SaveAsync(Settings.ToSettings() with { ModelDirectory = folder, SetupCompleted = false }, ct); Settings.Load(workspace.Settings.Current);
            long missing = workspace.Catalog.Models.SelectMany(m => m.Files).DistinctBy(f => f.Path).Where(f => !File.Exists(Path.Combine(folder, f.Path))).Sum(f => f.Size);
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            if (drive.IsReady && drive.AvailableFreeSpace < missing + 536870912) throw new IOException("Not enough disk space for the selected models.");
            var report = ReportProgress();
            for (int i = 0; i < workspace.Catalog.Models.Count; i++)
            {
                var model = workspace.Catalog.Models[i]; int index = i;
                report.Report(new((double)i / workspace.Catalog.Models.Count, $"Preparing {i+1}/{workspace.Catalog.Models.Count}: {model.DisplayName}"));
                await workspace.Models.InstallAsync(model.Id, new InlineProgress(p => report.Report(p with {
                    Fraction = (index + p.Fraction) / workspace.Catalog.Models.Count, Message = model.DisplayName + " · " + p.Message })), ct);
            }
            foreach (var name in new[] { "audiocpp_server", "audiocpp_cli", "llama-server", "ffmpeg", "ffprobe" })
            {
                string executable = workspace.Locator.Locate(name);
                var result = await workspace.Processes.RunAsync(new(executable, [name.StartsWith("ff") ? "-version" : "--version"], TimeSpan.FromSeconds(30)), ct);
                if (result.ExitCode != 0) throw new InvalidOperationException("Runtime check failed: " + name);
                AppendActivity(name + " · " + (result.StandardOutput + result.StandardError).Trim());
            }
            await workspace.Settings.SaveAsync(workspace.Settings.Current with { SetupCompleted = true }, ct); Settings.Load(workspace.Settings.Current);
            await RefreshModelsCoreAsync(ct); SetupSummary = L["Ready · all recommended models verified"]; Status = SetupSummary; Progress = 100;
        }, () => operation?.Cancel());
    });
    private sealed class InlineProgress(Action<EngineProgress> report) : IProgress<EngineProgress> { public void Report(EngineProgress value) => report(value); }

    private async Task<string> CopySourceAsync(string input, string directory, CancellationToken ct)
    {
        if (workspace?.Settings.Current.CopySourceVideo != true) return input;
        string folder = Path.Combine(directory, "assets", "source"); Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "source" + Path.GetExtension(input).ToLowerInvariant());
        if (Path.GetFullPath(input) == Path.GetFullPath(output)) return input;
        string temp = output + ".partial";
        try
        {
            Status = L["Copying source video into project…"];
            await using (var source = File.OpenRead(input))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                await source.CopyToAsync(target, ct);
            File.Move(temp, output, true); return output;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task ExportWithDialogAsync(CancellationToken ct)
    {
        if (session.Project.Source is null) throw new InvalidOperationException("Import a video before exporting.");
        if (!await SaveCoreAsync(ct)) return;
        string directory = session.DirectoryPath!;
        string destination = Path.Combine(directory, "exports", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string profile = Settings.DefaultExportProfile; bool original = false, subtitles = true;
        int pending = session.Project.Segments.Count(s => !s.ExcludedFromDubbing && (s.GeneratedSpeech is null || s.DirtyStages.Contains(Dubbing.Core.Pipeline.PipelineStage.Tts)));
        var options = new PipelineDialogOptions { OperationTitle = "Export", ConfigureFields = panel =>
        {
            panel.Children.Add(Label(L.Format("Segments: {0} · Missing or outdated speech: {1}", session.Project.Segments.Count, pending)));
            panel.Children.Add(Label("Export uses the timeline volume and mute settings. Review the mix before exporting."));
            panel.Children.Add(Label("Export profile"));
            var profiles = new ComboBox { ItemsSource = ExportOptions.Profiles, SelectedItem = profile, HorizontalAlignment = HorizontalAlignment.Stretch };
            profiles.SelectionChanged += (_, _) => profile = (string)profiles.SelectedItem!; panel.Children.Add(profiles);
            FolderField(panel, "Empty export folder", destination, value => destination = value);
            var keep = new CheckBox { Content = L["Keep original audio as alternative tracks"] }; keep.IsCheckedChanged += (_, _) => original = keep.IsChecked == true; panel.Children.Add(keep);
            var subs = new CheckBox { Content = L["Embed selectable subtitles"], IsChecked = true }; subs.IsCheckedChanged += (_, _) => subtitles = subs.IsChecked == true; panel.Children.Add(subs);
            panel.Children.Add(Label("MKV preserves the video stream. MP4 uses H.264. Audio and editing profiles export WAV and SRT files."));
        } };
        await dialogs.RunPipelineAsync(options, async () =>
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose an export folder.");
            try { await exporter.ExportAsync(session.Project, directory, ReportProgress(), ct, new() { Directory = destination, Profile = profile, KeepOriginalAudio = original, EmbedSubtitles = subtitles }); }
            finally { await session.OpenAsync(directory, CancellationToken.None); Refresh(); }
            Status = L.Format("Exported: {0}", destination);
        }, () => operation?.Cancel());
    }
}
