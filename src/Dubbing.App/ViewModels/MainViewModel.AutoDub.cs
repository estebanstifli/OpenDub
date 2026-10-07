using Avalonia.Controls;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using Dubbing.App.Services;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand] private async Task AutoDubAsync() => await RunAsync(async ct =>
    {
        if (workspace is null) return;
        bool resume = session.Project.Source is not null && session.DirectoryPath is not null;
        string? input = resume ? session.Project.Source!.Path : await dialogs.OpenVideoAsync();
        if (input is null) return;
        if (!File.Exists(input)) throw new FileNotFoundException("Locate the source video by reopening the project.");
        var info = await workspace.Media.ProbeAsync(input, ct);
        var tracks = AudioPreparationService.Discover(info, []);
        if (tracks.Count == 0) throw new InvalidOperationException("The video contains no audio track.");
        var selected = tracks.FirstOrDefault(t => t.Index == session.Project.Settings.AnalysisStreamIndex) ?? tracks.First();
        string language = resume ? session.Project.Settings.TargetLanguage : Settings.DefaultTargetLanguage;
        string sourceLanguage = resume ? session.Project.Settings.SourceLanguage : "en";
        bool separate = resume ? session.Project.Settings.UseSeparation : true;
        string profile = Settings.DefaultExportProfile;
        string destination = Path.Combine(session.DirectoryPath ?? workspace.Settings.Current.EffectiveProjectsDirectory, "exports", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var options = new PipelineDialogOptions { OperationTitle = "Auto dub", ConfigureFields = panel =>
        {
            panel.Children.Add(Label(Path.GetFileName(input)));
            panel.Children.Add(Label(resume ? "Resume the current project from its saved segments. Import another video to start a new project." : "Analyze, translate, generate speech and export automatically."));
            panel.Children.Add(Label("Input language")); var source = new TextBox { Text = sourceLanguage, IsEnabled = !resume };
            source.TextChanged += (_, _) => sourceLanguage = source.Text ?? "en"; panel.Children.Add(source);
            panel.Children.Add(Label("Target language")); var lang = new ComboBox { ItemsSource = OutputLanguages, SelectedItem = language, IsEnabled = !resume };
            lang.SelectionChanged += (_, _) => language = (string)lang.SelectedItem!; panel.Children.Add(lang);
            panel.Children.Add(Label("Audio track"));
            var track = new ComboBox { ItemsSource = tracks.Select(t => $"#{t.Index} · {t.Language ?? "und"} · {t.Title} · {t.Channels} ch").ToArray(), SelectedIndex = tracks.ToList().IndexOf(selected), IsEnabled = !resume, HorizontalAlignment = HorizontalAlignment.Stretch };
            track.SelectionChanged += (_, _) => { if (track.SelectedIndex >= 0) selected = tracks[track.SelectedIndex]; }; panel.Children.Add(track);
            var separation = new CheckBox { Content = L["Separate voices from music and effects"], IsChecked = separate, IsEnabled = !resume };
            separation.IsCheckedChanged += (_, _) => separate = separation.IsChecked == true; panel.Children.Add(separation);
            var profiles = new ComboBox { ItemsSource = ExportOptions.Profiles, SelectedItem = profile }; profiles.SelectionChanged += (_, _) => profile = (string)profiles.SelectedItem!;
            panel.Children.Add(Label("Export profile")); panel.Children.Add(profiles);
            FolderField(panel, "Empty export folder", destination, value => destination = value);
            panel.Children.Add(Label("Progress is saved after each stage and segment. If a segment needs attention, correct it in Studio and run Auto dub again."));
        } };
        await dialogs.RunPipelineAsync(options, async () =>
        {
            if (!OutputLanguages.Contains(language) || string.IsNullOrWhiteSpace(sourceLanguage) || string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Choose valid languages and an export folder.");
            if (!resume)
            {
                if (!await ResolveUnsavedAsync(ct)) return;
                var project = CreateDefaultProject() with { Name = Path.GetFileNameWithoutExtension(input),
                    AudioTracks = tracks.Select(t => t with { Role = t.Index == selected.Index ? AudioTrackRole.Mixed : AudioTrackRole.Ignore }).ToArray() };
                project = project with { Settings = project.Settings with { SourceLanguage = sourceLanguage, TargetLanguage = language, UseSeparation = separate, AnalysisStreamIndex = selected.Index } };
                var folder = await dialogs.NewProjectDirectoryAsync(project.Name); if (folder is null) return;
                input = await CopySourceAsync(input, folder, ct);
                StopPlayback(); session.New(project); await session.SaveAsync(folder, ct);
            }
            else if (!await SaveCoreAsync(ct)) return;
            string directory = session.DirectoryPath!;
            // Auto dub always exports the generated dialogue plus available background.
            session.Update(session.Project with { Settings = session.Project.Settings with { OriginalVoiceMuted = true, DubMuted = false, BackgroundMuted = false } });
            await session.SaveAsync(null, ct);
            try
            {
                var service = new AutoDubService(importer, translator, speech, exporter, new SessionStore(session));
                await service.RunAsync(session.Project, input, directory, new() { Directory = destination, Profile = profile }, ReportProgress(), ct);
            }
            finally { await session.OpenAsync(directory, CancellationToken.None); Refresh(); await workspace.Runtimes.ReleaseAsync(); }
            await LoadWaveformAsync(ct); Status = L.Format("Exported: {0}", destination);
        }, () => operation?.Cancel());
    });
    // The service uses the same persistent project store through the session for checkpoints.
    private sealed class SessionStore(Dubbing.Application.Projects.ProjectSession session) : IProjectStore
    {
        public Task SaveAsync(DubbingProject project, string directory, CancellationToken ct) { session.Update(project); return session.SaveAsync(directory, ct); }
        public async Task<DubbingProject> LoadAsync(string directory, CancellationToken ct) { await session.OpenAsync(directory, ct); return session.Project; }
    }
}
