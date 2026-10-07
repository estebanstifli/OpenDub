using System.Security.Cryptography;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Infrastructure.Models;

public sealed class VerifiedModelManager(ModelCatalog catalog, SettingsStore settings, IProcessRunner runner, HttpClient? http = null) : IModelManager, IDisposable
{
    private readonly HttpClient client = http ?? new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ModelDescriptor>>(catalog.Models.Select(m => new ModelDescriptor(m.Id, m.Provider, m.DisplayName,
            m.Version, m.License, m.Source, m.Files.Count > 0 && m.Files.All(f => File.Exists(PathFor(f)) && new FileInfo(PathFor(f)).Length == f.Size),
            m.Files.Sum(f => f.Size))).ToArray());
    }
    public async Task InstallAsync(string modelId, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        var model = Find(modelId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (model.Provider == "audio.cpp" && settings.Current.ModelManagerPath is { Length: > 0 } manager)
            {
                progress?.Report(new(0, "Installing model with native package manager…"));
                var result = await runner.RunAsync(new(manager, ["install", model.Id, "--models-dir", settings.Current.ModelDirectory,
                    "--repository-root", Path.Combine(AppContext.BaseDirectory, "runtime-specs")]), cancellationToken);
                if (result.ExitCode != 0) throw new EngineException("Native model installation failed. " + result.StandardError);
                if (!await VerifyAsync(modelId, cancellationToken)) throw new EngineException("Installed model does not match the pinned catalog checksums.");
            }
            else
            {
                // Release builds without the native manager use the provider's pinned file manifest.
                long total = model.Files.Sum(f => f.Size), completed = 0;
                foreach (var file in model.Files)
                {
                    string path = PathFor(file);
                    if (await VerifyFileAsync(file, cancellationToken)) { completed += file.Size; continue; }
                    if (file.Url.Scheme != Uri.UriSchemeHttps || file.Sha256.Length != 64 || file.Size <= 0)
                        throw new InvalidDataException("Downloads require HTTPS, size and a pinned SHA-256.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await ResumableDownload.SaveAsync(client, file.Url, path, file.Size, file.Sha256,
                        read => progress?.Report(new((double)(completed + read) / total, $"Downloading {Path.GetFileName(path)}…")), cancellationToken);
                    completed += file.Size;
                }
            }
            progress?.Report(new(1, "Model installed and verified."));
        }
        finally { gate.Release(); }
    }
    public async Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken)
    {
        foreach (var file in Find(modelId).Files) if (!await VerifyFileAsync(file, cancellationToken)) return false;
        return true;
    }
    public async Task RemoveAsync(string modelId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var model = Find(modelId);
            foreach (var file in model.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Preserve shared sidecars that another installed model still needs.
                if (catalog.Models.Any(m => m.Id != modelId && m.Files.Any(f => f.Path == file.Path) && m.Files.All(f => File.Exists(PathFor(f))))) continue;
                string path = PathFor(file); if (File.Exists(path)) File.Delete(path);
            }
        }
        finally { gate.Release(); }
    }
    private CatalogModel Find(string id) => catalog.Models.Single(m => m.Id == id);
    private string PathFor(ModelFile file) => ModelCatalog.SafePath(settings.Current.ModelDirectory, file.Path);
    private async Task<bool> VerifyFileAsync(ModelFile file, CancellationToken ct)
    {
        string path = PathFor(file);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
