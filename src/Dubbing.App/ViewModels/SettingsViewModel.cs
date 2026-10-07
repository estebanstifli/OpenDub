using CommunityToolkit.Mvvm.ComponentModel;
using Dubbing.Infrastructure.Configuration;

namespace Dubbing.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<string> TranslationBackends { get; } = ["auto", "cpu", "cuda", "metal"];
    [ObservableProperty] private string translationBackend = "auto";
    private AppSettings original = new();
    [ObservableProperty] private bool copySourceVideo = true;
    [ObservableProperty] private string defaultTargetLanguage = "es";
    [ObservableProperty] private string defaultExportProfile = "Recommended MP4";
    public IReadOnlyList<string> ExportProfiles => Dubbing.Application.Pipeline.ExportOptions.Profiles;
    [ObservableProperty] private string uiLanguage = "en";
    [ObservableProperty] private string modelDirectory = "";
    [ObservableProperty] private string projectsDirectory = "";
    [ObservableProperty] private string backend = "auto";
    [ObservableProperty] private int threads = 4;
    [ObservableProperty] private int device;
    [ObservableProperty] private string audioServerPath = "";
    [ObservableProperty] private string audioCliPath = "";
    [ObservableProperty] private string llamaServerPath = "";
    [ObservableProperty] private string translationModelPath = "";
    [ObservableProperty] private string fFmpegPath = "";
    [ObservableProperty] private string fFprobePath = "";
    public IReadOnlyList<string> Backends { get; } = ["auto", "cpu", "cuda", "metal", "vulkan", "hip", "metal"];
    public void Load(AppSettings settings)
    {
        CopySourceVideo = settings.CopySourceVideo; DefaultTargetLanguage = settings.DefaultTargetLanguage; DefaultExportProfile = settings.DefaultExportProfile;
        original = settings; TranslationBackend = settings.TranslationBackend;
        ProjectsDirectory = settings.ProjectsDirectory ?? "";
        UiLanguage = settings.UiLanguage; ModelDirectory = settings.ModelDirectory; Backend = settings.Backend;
        Threads = settings.Threads; Device = settings.Device; AudioServerPath = settings.AudioServerPath ?? "";
        AudioCliPath = settings.AudioCliPath ?? ""; LlamaServerPath = settings.LlamaServerPath ?? "";
        TranslationModelPath = settings.TranslationModelPath ?? ""; FFmpegPath = settings.FFmpegPath ?? ""; FFprobePath = settings.FFprobePath ?? "";
    }
    public AppSettings ToSettings() => original with
    {
        CopySourceVideo = CopySourceVideo, DefaultTargetLanguage = DefaultTargetLanguage, DefaultExportProfile = DefaultExportProfile,
        TranslationBackend = TranslationBackend, UiLanguage = UiLanguage, ModelDirectory = ModelDirectory, Backend = Backend, Threads = Threads, Device = Device,
        ProjectsDirectory = Empty(ProjectsDirectory),
        AudioServerPath = Empty(AudioServerPath), AudioCliPath = Empty(AudioCliPath), LlamaServerPath = Empty(LlamaServerPath),
        TranslationModelPath = Empty(TranslationModelPath), FFmpegPath = Empty(FFmpegPath), FFprobePath = Empty(FFprobePath)
    };
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
