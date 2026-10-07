using System.Text.Json;
using System.Text.Json.Serialization;
using Dubbing.Core.Engines;

namespace Dubbing.Infrastructure.Models;

public sealed record ModelFile(string Path, Uri Url, long Size, string Sha256);
public sealed record CatalogModel
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string DisplayName { get; init; }
    public required string Family { get; init; }
    public required EngineTask Task { get; init; }
    public required string EntryPoint { get; init; }
    public string Version { get; init; } = "";
    public string? License { get; init; }
    public Uri? Source { get; init; }
    public HashSet<string> Languages { get; init; } = ["*"];
    public EngineCapabilities Capabilities { get; init; } = new();
    public Dictionary<string, string> FieldMappings { get; init; } = [];
    public Dictionary<string, string> LanguageNames { get; init; } = [];
    public List<ModelFile> Files { get; init; } = [];
    public IReadOnlyList<EngineOptionDescriptor> Options { get; init; } = [];
    public EngineDescriptor Descriptor => new(Provider, $"{Provider}/{Id}", DisplayName, Task, Languages, Capabilities, new(), License, Version);
}

public sealed class ModelCatalog
{
    public IReadOnlyList<CatalogModel> Models { get; }
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public ModelCatalog(string directory)
    {
        Models = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .SelectMany(path => JsonSerializer.Deserialize<List<CatalogModel>>(File.ReadAllText(path), JsonOptions) ?? []).ToArray() : [];
        if (Models.Select(m => m.Id).Distinct().Count() != Models.Count) throw new InvalidDataException("Duplicate catalog model ID.");
        foreach (var model in Models)
        {
            if (model.Files.Count == 0 || !model.Files.Any(f => f.Path == model.EntryPoint)) throw new InvalidDataException("Catalog model requires a pinned entry point.");
            foreach (var file in model.Files)
            {
                SafePath(Path.GetTempPath(), file.Path);
                if (file.Size <= 0 || file.Url.Scheme != "https" || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Invalid pinned catalog file.");
            }
        }
    }
    public string ModelPath(CatalogModel model, string root) => SafePath(root, model.EntryPoint);
    public static string SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(fullRoot)) fullRoot += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Catalog paths must stay within the model directory.");
        return full;
    }
}
