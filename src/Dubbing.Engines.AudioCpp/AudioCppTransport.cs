using System.Net.Http.Json;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Models;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Engines.AudioCpp;

public interface IAudioCppTransport
{
    Task<JsonElement> JsonAsync(CatalogModel model, string route, object request, CancellationToken ct);
    Task SpeechAsync(CatalogModel model, object request, string output, CancellationToken ct);
    Task SeparateAsync(CatalogModel model, string input, string outputDirectory, CancellationToken ct);
}

public sealed class AudioCppTransport(SettingsStore settings, ModelCatalog catalog, IRuntimeLocator locator,
    IProcessRunner runner, RuntimeCoordinator coordinator) : IAudioCppTransport, IDisposable
{
    private readonly LocalServerHost host = new();
    private readonly string audioWorkingDirectory = Path.Combine(Path.GetTempPath(), "opendub-audio-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? verifiedExecutable;
    private string buildInfo = "";
    private string? residentModel;
    private bool? nvidiaAvailable;
    public Task<JsonElement> JsonAsync(CatalogModel model, string route, object request, CancellationToken ct) =>
        coordinator.RunAsync("audio.cpp", ReleaseAsync, host.Stop, async () =>
        {
            var endpoint = await EnsureAsync(model, ct);
            using var staged = await NativeAudioRequest.CreateAsync(audioWorkingDirectory, request, ct);
            ActivityJournal.Write($"audio.cpp · {model.DisplayName} · {route}");
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, route)) { Content = JsonContent.Create(staged.Body) };
            using var response = await SendAsync(message, ct);
            await CheckAsync(response, ct);
            residentModel = model.Id;
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }, ct);
    public Task SpeechAsync(CatalogModel model, object request, string output, CancellationToken ct) =>
        coordinator.RunAsync("audio.cpp", ReleaseAsync, host.Stop, async () =>
        {
            var endpoint = await EnsureAsync(model, ct);
            using var staged = await NativeAudioRequest.CreateAsync(audioWorkingDirectory, request, ct);
            ActivityJournal.Write($"audio.cpp · generating speech with {model.DisplayName}");
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/audio/speech")) { Content = JsonContent.Create(staged.Body) };
            using var response = await SendAsync(message, ct);
            await CheckAsync(response, ct);
            residentModel = model.Id;
            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            try
            {
                await using (var stream = File.Create(temporary)) await response.Content.CopyToAsync(stream, ct);
                ct.ThrowIfCancellationRequested(); File.Move(temporary, output, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        }, ct);
    public Task SeparateAsync(CatalogModel model, string input, string outputDirectory, CancellationToken ct) =>
        coordinator.RunAsync("audio.cpp-cli", () => { }, async () =>
        {
            string path = RequireModel(model);
            string executable = await FindExecutableAsync("audiocpp_cli", settings.Current.AudioCliPath, ct);
            var version = await runner.RunAsync(new(executable, ["--version"]), ct);
            RequireVersion(version.StandardOutput);
            string backend = SelectBackend(version.StandardOutput);
            ActivityJournal.Write($"audio.cpp CLI · separation · backend={backend} · device={settings.Current.Device}.");
            Directory.CreateDirectory(outputDirectory);
            // The Windows CLI uses narrow file paths. Pass only ASCII relative media paths;
            // ProcessStartInfo supplies the working directory through the Unicode Windows API.
            string scratch = Path.Combine(Path.GetTempPath(), "dubbing-separation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                await using (var source = File.OpenRead(input))
                await using (var target = File.Create(Path.Combine(scratch, "input.wav")))
                    await source.CopyToAsync(target, ct);
                ActivityJournal.Write("audio.cpp CLI · using Unicode-safe audio staging.");
                var result = await runner.RunAsync(new(executable, ["--task", "sep", "--family", model.Family,
                    "--model", Path.GetFullPath(path), "--backend", backend, "--threads", settings.Current.Threads.ToString(), "--device", settings.Current.Device.ToString(),
                    "--audio", "input.wav", "--out-dir", "stems"], WorkingDirectory: scratch), ct);
                if (result.ExitCode != 0) throw new EngineException($"Source separation failed: {result.StandardError}");
                foreach (string stem in new[] { "vocals.wav", "instrumental.wav" })
                    if (!File.Exists(Path.Combine(scratch, "stems", stem))) throw new EngineException($"Source separation did not produce {stem}.");
                foreach (string stem in new[] { "vocals.wav", "instrumental.wav" })
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(Path.Combine(scratch, "stems", stem), Path.Combine(outputDirectory, stem), true);
                }
            }
            finally { Directory.Delete(scratch, true); }
            return true;
        }, ct);
    public async Task<JsonElement> ListRuntimeModelsAsync(CancellationToken ct)
    {
        var first = catalog.Models.First(m => m.Provider == "audio.cpp" && File.Exists(catalog.ModelPath(m, settings.Current.ModelDirectory)));
        return await coordinator.RunAsync("audio.cpp", ReleaseAsync, host.Stop, async () =>
        {
            var endpoint = await EnsureAsync(first, ct, forInference: false);
            return await http.GetFromJsonAsync<JsonElement>(new Uri(endpoint, "v1/models"), ct);
        }, ct);
    }
    private async Task<Uri> EnsureAsync(CatalogModel selected, CancellationToken ct, bool forInference = true)
    {
        RequireModel(selected);
        string executable = await FindExecutableAsync("audiocpp_server", settings.Current.AudioServerPath, ct);
        if (verifiedExecutable != executable)
        {
            var version = await runner.RunAsync(new(executable, ["--version"]), ct);
            RequireVersion(version.StandardOutput); buildInfo = version.StandardOutput; verifiedExecutable = executable;
        }
        var config = new
        {
            host = "127.0.0.1", backend = SelectBackend(buildInfo), device = settings.Current.Device,
            threads = settings.Current.Threads, lazy_load = true, max_loaded_models = 1, ui_management = true, log_request_body = false,
            models = catalog.Models.Where(m => m.Provider == "audio.cpp" && IsInstalled(m))
                .Select(m => new { id = m.Id, family = m.Family, path = Path.GetFullPath(catalog.ModelPath(m, settings.Current.ModelDirectory)),
                    task = m.Task switch { Dubbing.Core.Engines.EngineTask.Transcription => "asr", Dubbing.Core.Engines.EngineTask.Alignment => "align",
                        Dubbing.Core.Engines.EngineTask.Diarization => "diar", Dubbing.Core.Engines.EngineTask.Separation => "sep", _ => "tts" }, mode = "offline" }).ToArray()
        };
        string content = JsonSerializer.Serialize(config);
        string configPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "audio-server.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, content, ct);
        var previousEndpoint = host.Endpoint;
        Directory.CreateDirectory(audioWorkingDirectory);
        var endpoint = await host.EnsureAsync(Path.GetFullPath(executable), content, port => ["--config", configPath, "--host", "127.0.0.1", "--port", port.ToString(), "--no-ui"], ct);
        if (previousEndpoint != endpoint) ActivityJournal.Write("audio.cpp · short WAV staging paths enabled.");
        if (previousEndpoint != endpoint) residentModel = null;
        if (!forInference) return endpoint;
        if (residentModel != selected.Id)
        {
            ActivityJournal.Write($"audio.cpp · backend={config.backend} · device={config.device} · threads={config.threads} · max resident models=1.");
            if (config.backend == "cpu") ActivityJournal.Write("audio.cpp · CPU inference; CUDA is not active for this runtime.");
            if (residentModel is not null) ActivityJournal.Write($"audio.cpp · evicting {residentModel} before loading {selected.DisplayName}.");
            ActivityJournal.Write($"audio.cpp · first request will load {selected.DisplayName}; subsequent requests reuse the resident model.");
        }
        else ActivityJournal.Write($"audio.cpp · reusing resident model {selected.DisplayName}.");
        return endpoint;
    }
    private async Task<string> FindExecutableAsync(string name, string? configured, CancellationToken ct)
    {
        string cpu = locator.Locate(name, configured);
        if (!string.IsNullOrWhiteSpace(configured) || !OperatingSystem.IsWindows() || settings.Current.Backend is not ("auto" or "cuda")) return cpu;
        string cuda = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cpu))!, "audio-cuda", Path.GetFileName(cpu));
        if (!File.Exists(cuda)) return cpu;
        if (nvidiaAvailable is null)
        {
            try
            {
                var gpu = await runner.RunAsync(new(locator.Locate("nvidia-smi"), ["--query-gpu=index,name,memory.total,memory.free", "--format=csv,noheader"], TimeSpan.FromSeconds(5)), ct);
                nvidiaAvailable = gpu.ExitCode == 0 && !string.IsNullOrWhiteSpace(gpu.StandardOutput);
                if (nvidiaAvailable.Value) ActivityJournal.Write("NVIDIA GPU (index, name, total/free VRAM): " + gpu.StandardOutput.Trim());
            }
            catch (Exception ex) when (ex is FileNotFoundException or TimeoutException or System.ComponentModel.Win32Exception) { nvidiaAvailable = false; }
        }
        return nvidiaAvailable == true || settings.Current.Backend == "cuda" ? cuda : cpu;
    }
    private async Task ReleaseAsync()
    {
        if (host.Endpoint is { } endpoint)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await http.PostAsync(new Uri(endpoint, "v1/tasks/unload_all_models"), null, timeout.Token);
                await CheckAsync(response, timeout.Token);
                ActivityJournal.Write("audio.cpp · unloaded resident models; server remains available.");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or EngineException)
            { ActivityJournal.Write("audio.cpp · unload could not complete; stopping owned server to release memory."); host.Stop(); }
        }
        residentModel = null;
    }
    private string SelectBackend(string buildInfo)
    {
        var configured = settings.Current.Backend;
        var enabled = buildInfo.Split('\n').FirstOrDefault(l => l.StartsWith("backends:", StringComparison.OrdinalIgnoreCase)) ?? "cpu";
        if (configured == "auto") return new[] { "cuda", "metal", "vulkan", "hip", "cpu" }.First(b => enabled.Contains(b, StringComparison.OrdinalIgnoreCase));
        if (!enabled.Contains(configured, StringComparison.OrdinalIgnoreCase)) throw new UnsupportedCapabilityException($"This audio.cpp build does not include {configured}.");
        return configured;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try { var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct); ActivityJournal.Write($"Audio request completed · HTTP {(int)response.StatusCode} · {timer.Elapsed.TotalSeconds:F1} s"); return response; }
        catch (HttpRequestException ex)
        {
            string detail = host.FailureDescription(); host.Stop();
            throw new EngineException(detail + " Review the current segment and its reference audio before retrying.", ex);
        }
    }
    private string RequireModel(CatalogModel model)
    {
        string path = catalog.ModelPath(model, settings.Current.ModelDirectory);
        if (!IsInstalled(model)) throw new ModelNotInstalledException($"Install all files for {model.DisplayName} in Models first.");
        return path;
    }
    private bool IsInstalled(CatalogModel model) => model.Files.All(f =>
    {
        string path = ModelCatalog.SafePath(settings.Current.ModelDirectory, f.Path);
        return File.Exists(path) && new FileInfo(path).Length == f.Size;
    });
    private static void RequireVersion(string text)
    { if (!text.Contains("audio.cpp 0.9.0", StringComparison.Ordinal)) throw new EngineException("This provider requires audio.cpp 0.9.0."); }
    internal static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new EngineException($"Local audio runtime returned {(int)response.StatusCode}: {error[..Math.Min(error.Length, 1500)]}");
        }
    }
    public void Dispose() { host.Dispose(); http.Dispose(); if (Directory.Exists(audioWorkingDirectory)) Directory.Delete(audioWorkingDirectory, true); }
}
