using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public sealed record ModelRow(ModelDescriptor Model, string State)
{
    public string DisplayName => Model.DisplayName;
    public string Detail => $"{Model.ProviderId} · {Model.SizeBytes / 1073741824.0:F2} GB · {Model.License}";
    public string Source => Model.Source?.ToString() ?? "";
}

public partial class MainViewModel
{
    public SettingsViewModel Settings { get; } = new();
    public IReadOnlyList<string> UiLanguages => L.AvailableLanguages;
    public ObservableCollection<ModelRow> Models { get; } = [];
    [ObservableProperty] private ModelRow? selectedModel;
    [ObservableProperty] private string diagnostics = "";

    [RelayCommand] private async Task RefreshModelsAsync() => await RunAsync(RefreshModelsCoreAsync);
    [RelayCommand] private async Task UnloadModelsAsync() => await RunAsync(async ct =>
    {
        if (workspace is not null) await workspace.Runtimes.ReleaseAsync();
        Status = L["Model memory released."];
    });
    private async Task RefreshModelsCoreAsync(CancellationToken ct)
    {
        if (workspace is null) return;
        string? selected = SelectedModel?.Model.Id;
        var models = await workspace.Models.ListAsync(ct);
        Models.Clear(); foreach (var model in models) Models.Add(new(model, L[model.Installed ? "Installed" : "Not installed"]));
        SelectedModel = Models.FirstOrDefault(m => m.Model.Id == selected) ?? Models.FirstOrDefault();
    }
    [RelayCommand] private async Task InstallModelAsync() => await RunAsync(async ct =>
    {
        if (workspace is null || SelectedModel is null) return;
        await workspace.Models.InstallAsync(SelectedModel.Model.Id, ReportProgress(), ct); await RefreshModelsCoreAsync(ct);
    });
    [RelayCommand] private async Task VerifyModelAsync() => await RunAsync(async ct =>
    {
        if (workspace is null || SelectedModel is null) return;
        Status = L[await workspace.Models.VerifyAsync(SelectedModel.Model.Id, ct) ? "Model files verified." : "Model missing or checksum mismatch. Reinstall it."];
    });
    [RelayCommand] private async Task RemoveModelAsync() => await RunAsync(async ct =>
    {
        if (workspace is null || SelectedModel is null || !await dialogs.ConfirmAsync("Remove selected model?")) return;
        await workspace.Runtimes.RunAsync("model-management", () => { }, async () =>
        { await workspace.Models.RemoveAsync(SelectedModel.Model.Id, ct); return true; }, ct);
        await RefreshModelsCoreAsync(ct);
    });
    [RelayCommand] private async Task UseTranslationModelAsync() => await RunAsync(async ct =>
    {
        if (workspace is null || SelectedModel?.Model.ProviderId != "llama.cpp") return;
        var model = workspace.Catalog.Models.Single(m => m.Id == SelectedModel.Model.Id);
        Settings.TranslationModelPath = workspace.Catalog.ModelPath(model, Settings.ModelDirectory);
        Edit(p => ProjectEditor.Invalidate(p with { Engines = p.Engines with { Translation = "llama.cpp/local-translator" } }, PipelineStage.Translation));
        Refresh();
        await workspace.Settings.SaveAsync(Settings.ToSettings(), ct); Status = L["Translation model selected."];
    });
    [RelayCommand] private async Task SaveSettingsAsync() => await RunAsync(async ct =>
    {
        if (workspace is null) return;
        if (workspace.Settings.Current.TranslationModelPath != Settings.ToSettings().TranslationModelPath)
            Edit(p => ProjectEditor.Invalidate(p, PipelineStage.Translation));
        await workspace.Settings.SaveAsync(Settings.ToSettings(), ct); L.Load(Settings.UiLanguage); Refresh();
        Status = L["Settings saved."];
    });
    [RelayCommand] private async Task BrowseSettingAsync(string property) => await RunAsync(async ct =>
    {
        var path = await dialogs.PickFileAsync("Browse"); if (path is null) return;
        typeof(SettingsViewModel).GetProperty(property)?.SetValue(Settings, path);
    });
    [RelayCommand] private async Task ApplyGlossaryAsync() => await RunAsync(ct =>
    {
        var glossary = JsonSerializer.Deserialize<Dictionary<string, string>>(GlossaryJson) ?? [];
        Edit(p => ProjectEditor.Invalidate(p with { Settings = p.Settings with { Glossary = glossary } }, PipelineStage.Translation));
        return Task.CompletedTask;
    });
    [RelayCommand] private async Task ClearCacheAsync() => await RunAsync(async ct =>
    {
        if (session.DirectoryPath is not { } directory || !await dialogs.ConfirmAsync("Delete generated audio and render cache?")) return;
        foreach (var relative in new[] { "generated", "cache", "renders" })
        {
            string path = Path.Combine(directory, relative);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        Edit(p => ProjectEditor.Invalidate(p with { Segments = p.Segments.Select(s => s with { GeneratedSpeech = null, FittedSpeech = null }).ToArray() }, PipelineStage.Tts));
        await session.SaveAsync(null, ct); Refresh();
    });
    [RelayCommand] private async Task InspectRuntimesAsync() => await RunAsync(async ct =>
    {
        if (workspace is null) return;
        var lines = new List<string> { $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · {Environment.ProcessorCount} logical processors", $"Memory available to .NET: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:F1} GB" };
        foreach (var (name, path) in new[] { ("audiocpp_cli", Settings.AudioCliPath), ("audiocpp_server", Settings.AudioServerPath), ("llama-server", Settings.LlamaServerPath), ("ffmpeg", Settings.FFmpegPath) })
        {
            try
            {
                var result = await workspace.Processes.RunAsync(new(workspace.Locator.Locate(name, string.IsNullOrWhiteSpace(path) ? null : path),
                    [name == "ffmpeg" ? "-version" : "--version"], TimeSpan.FromSeconds(15)), ct);
                lines.Add(name + ": " + (result.StandardOutput + result.StandardError).Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { lines.Add(name + ": " + ex.Message); }
        }
        Diagnostics = string.Join(Environment.NewLine + Environment.NewLine, lines);
    });
}
