using System.Text.Json;

namespace Dubbing.Infrastructure.Configuration;

public sealed record AppSettings
{
    public bool SetupCompleted { get; init; }
    public bool CopySourceVideo { get; init; } = true;
    public string DefaultTargetLanguage { get; init; } = "es";
    public string DefaultExportProfile { get; init; } = "Recommended MP4";
    public string UiLanguage { get; init; } = "en";
    public string? ProjectsDirectory { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveProjectsDirectory => Path.GetFullPath(string.IsNullOrWhiteSpace(ProjectsDirectory) ? AppPaths.ProjectsRoot : ProjectsDirectory);
    public string ModelDirectory { get; init; } = Path.Combine(AppPaths.DataRoot, "models");
    public string? AudioServerPath { get; init; }
    public string? AudioCliPath { get; init; }
    public string? ModelManagerPath { get; init; }
    public string? LlamaServerPath { get; init; }
    public string? TranslationModelPath { get; init; }
    public string? FFmpegPath { get; init; }
    public string? FFprobePath { get; init; }
    public string TranslationBackend { get; init; } = "auto";
    public string Backend { get; init; } = "auto";
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int Device { get; init; }
    public bool PrivacyMode { get; init; } = true;
}

public sealed class SettingsStore
{
    public string FilePath { get; }
    public AppSettings Current { get; private set; }
    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(AppPaths.DataRoot, "settings.json");
        if (filePath is null && !AppPaths.IsPortable && !File.Exists(FilePath))
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DubbingApp", "settings.json");
            if (File.Exists(legacy)) { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.Copy(legacy, FilePath); }
        }
        Current = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("Invalid application settings.") : new();
        Current = MapPaths(Current, AppPaths.Resolve);
    }
    private static AppSettings MapPaths(AppSettings settings, Func<string, string> map)
    {
        string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : map(value);
        return settings with { ModelDirectory = map(settings.ModelDirectory), ProjectsDirectory = Optional(settings.ProjectsDirectory),
            TranslationModelPath = Optional(settings.TranslationModelPath), AudioServerPath = Optional(settings.AudioServerPath),
            AudioCliPath = Optional(settings.AudioCliPath), ModelManagerPath = Optional(settings.ModelManagerPath),
            LlamaServerPath = Optional(settings.LlamaServerPath), FFmpegPath = Optional(settings.FFmpegPath), FFprobePath = Optional(settings.FFprobePath) };
    }

    public IDisposable OverrideForOperation(AppSettings settings)
    {
        var previous = Current; Current = settings;
        return new RestoreScope(() => Current = previous);
    }
    private sealed class RestoreScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        if (!new[] { "auto", "cpu", "cuda", "metal" }.Contains(settings.TranslationBackend) || settings.Threads < 1 || settings.Device < 0 || !new[] { "auto", "cpu", "cuda", "vulkan", "hip", "metal" }.Contains(settings.Backend))
            throw new ArgumentException("Invalid runtime settings.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        settings = MapPaths(settings, AppPaths.Resolve);
        var stored = AppPaths.IsPortable ? MapPaths(settings, AppPaths.StorePath) : settings;
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(temp, FilePath, true); Current = settings;
    }
}
