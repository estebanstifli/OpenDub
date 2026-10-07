using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.Application.Projects;
using Dubbing.Core.Engines;
using Dubbing.Core.Pipeline;
using Dubbing.Engines.LlamaCpp;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<string> OllamaModels { get; } = [];
    [ObservableProperty] private string? selectedOllamaModel;
    [ObservableProperty] private string ollamaStatus = "";
    [ObservableProperty] private bool useSourceVoicePerSegment = true;
    public bool IsOllamaSelected => session.Project.Engines.Translation == OllamaTranslationEngine.Id;
    partial void OnSelectedOllamaModelChanged(string? value)
    {
        if (value is null) return;
        Edit(p => ProjectEditor.Invalidate(p with { Settings = p.Settings with { TranslationModelId = value } }, PipelineStage.Translation));
    }
    partial void OnUseSourceVoicePerSegmentChanged(bool value) => Edit(p => ProjectEditor.Invalidate(
        p with { Settings = p.Settings with { UseSourceVoicePerSegment = value } }, PipelineStage.Tts));

    [RelayCommand] private async Task RefreshOllamaModelsAsync() => await RunAsync(LoadOllamaModelsAsync);
    private async Task LoadOllamaModelsAsync(CancellationToken ct)
    {
        try
        {
            var engine = registry.GetEngine<ITranslationEngine>(OllamaTranslationEngine.Id) as OllamaTranslationEngine;
            if (engine is null) return;
            var models = await engine.ListModelsAsync(ct);
            bool previous = refreshing;
            refreshing = true;
            try
            {
                var saved = session.Project.Settings.TranslationModelId;
                OllamaModels.Clear(); foreach (var model in models) OllamaModels.Add(model);
                if (saved is not null && !OllamaModels.Contains(saved)) OllamaModels.Add(saved);
                SelectedOllamaModel = saved;
                OllamaStatus = saved is not null && !models.Contains(saved) ? L["The saved Ollama model is unavailable."] :
                    models.Count == 0 ? L["No local chat models found in Ollama."] : L["Local Ollama models loaded."];
            }
            finally { refreshing = previous; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { OllamaStatus = L[ex.Message]; }
    }
}
