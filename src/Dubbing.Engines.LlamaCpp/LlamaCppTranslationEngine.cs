using System.Net.Http.Json;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Engines.LlamaCpp;

public sealed class LlamaCppTranslationEngine(SettingsStore settings, IRuntimeLocator locator, RuntimeCoordinator coordinator, IProcessRunner runner) : ITranslationEngine, IDisposable
{
    private readonly LocalServerHost host = new();
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? verifiedExecutable;
    private string? runtimeKey;
    private (string Path, bool Cuda, bool Metal, string Device) runtime;
    public EngineDescriptor Descriptor { get; } = new("llama.cpp", "llama.cpp/local-translator", "Local translation (llama.cpp)",
        EngineTask.Translation, new HashSet<string> { "*" }, new(), new(), "MIT runtime; model license varies", "v0.6.0");

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
        coordinator.RunAsync("llama.cpp", host.Stop, async () =>
        {
            string model = string.IsNullOrWhiteSpace(settings.Current.TranslationModelPath)
                ? Path.Combine(settings.Current.ModelDirectory, "translation", "Qwen3-4B-Q4_K_M.gguf") : settings.Current.TranslationModelPath;
            if (!File.Exists(model)) throw new ModelNotInstalledException("Install Qwen3 4B Q4_K_M in Models, or select a translation GGUF in Settings.");
            ActivityJournal.Write("llama.cpp · translation model: " + Path.GetFileName(model));
            var chosen = await ResolveRuntimeAsync(cancellationToken);
            string executable = chosen.Path;
            if (verifiedExecutable != executable)
            {
                var version = await runner.RunAsync(new(executable, ["--version"], TimeSpan.FromSeconds(15)), cancellationToken);
                if (version.ExitCode != 0 || !(version.StandardOutput + version.StandardError).Contains("d81235049", StringComparison.Ordinal))
                    throw new EngineException("This provider requires llama.cpp b11429 (d81235049), the pinned v0.6.0 build.");
                verifiedExecutable = executable;
            }
            string layers = chosen.Cuda || chosen.Metal ? "auto" : "0";
            ActivityJournal.Write($"llama.cpp · requested={settings.Current.TranslationBackend} · selected={(chosen.Cuda ? "cuda" : chosen.Metal ? "metal" : "cpu")} · device={chosen.Device} · gpu-layers={layers}.");
            var endpoint = await host.EnsureAsync(executable, model + layers + settings.Current.Threads + ":" + settings.Current.Device,
                port => ["--model", model, "--host", "127.0.0.1", "--port", port.ToString(), "--ctx-size", "8192",
                    "--parallel", "1", "--verbosity", "4", "--device", chosen.Device, "--threads", settings.Current.Threads.ToString(), "--gpu-layers", layers], cancellationToken);
            string payload = JsonSerializer.Serialize(new { segmentId = request.SegmentId, text = request.Text,
                sourceLanguage = request.SourceLanguage, targetLanguage = request.TargetLanguage,
                previous = request.PreviousSegments, next = request.NextSegments, speaker = request.SpeakerId,
                targetSeconds = request.TargetDuration?.TotalSeconds, glossary = request.Glossary, style = request.StyleInstructions });
            string system = TranslationPrompts.Build(request.Prompt);
            Exception? last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var response = await http.PostAsJsonAsync(new Uri(endpoint, "v1/chat/completions"), new
                {
                    model = "local", temperature = 0.2, max_tokens = 2048, stream = false,
                    messages = new[] { new { role = "system", content = system }, new { role = "user", content = payload } },
                    response_format = new { type = "json_object", schema = new
                    { type = "object", properties = new { segmentId = new { type = "string", @enum = new[] { request.SegmentId } }, translation = new { type = "string", minLength = 1 }, language = new { type = "string", @enum = new[] { request.TargetLanguage } } },
                        required = new[] { "segmentId", "translation", "language" }, additionalProperties = false } }
                }, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    last = new EngineException($"Local translator returned {(int)response.StatusCode}.");
                    if ((int)response.StatusCode < 500) throw last;
                }
                else
                {
                    var root = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    try { return Parse(root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!, request); }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or EngineException) { last = ex; }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
            }
            throw new EngineException("The local translator did not return a valid translation after three attempts.", last);
        }, cancellationToken);

    private async Task<(string Path, bool Cuda, bool Metal, string Device)> ResolveRuntimeAsync(CancellationToken ct)
    {
        string key = settings.Current.TranslationBackend + "|" + settings.Current.LlamaServerPath + "|" + settings.Current.Device;
        if (runtimeKey == key) return runtime;
        string mode = settings.Current.TranslationBackend;
        if (mode != "cpu")
        {
            try
            {
                string candidate = OperatingSystem.IsMacOS() || mode == "metal" ? locator.Locate("llama-server", settings.Current.LlamaServerPath) : locator.LocateLlamaCuda(settings.Current.LlamaServerPath);
                var devices = await runner.RunAsync(new(candidate, ["--list-devices"], TimeSpan.FromSeconds(OperatingSystem.IsMacOS() || mode == "metal" ? 60 : 20)), ct);
                string output = devices.StandardOutput + devices.StandardError;
                ActivityJournal.Write("llama.cpp · devices: " + output.Trim());
                if (mode != "metal" && devices.ExitCode == 0 && FindGpuDevice(output, "cuda", settings.Current.Device) is { } cuda)
                { runtimeKey = key; return runtime = (candidate, true, false, cuda); }
                if (devices.ExitCode == 0 && mode is "auto" or "metal" && FindGpuDevice(output, "metal", settings.Current.Device) is { } metal)
                { runtimeKey = key; return runtime = (candidate, false, true, metal); }
                if (mode == "metal") throw new EngineException("Metal was requested, but llama.cpp reported no Metal device.");
                if (mode == "cuda") throw new EngineException("CUDA was requested for translation, but llama.cpp reported no CUDA devices. Check the CUDA runtime and NVIDIA driver.");
            }
            catch (Exception ex) when (mode == "auto" && ex is not OperationCanceledException)
            { ActivityJournal.Write("llama.cpp · GPU unavailable: " + ex.Message); }
        }
        string cpu = locator.Locate("llama-server", settings.Current.LlamaServerPath);
        ActivityJournal.Write("llama.cpp · translation uses CPU.");
        runtimeKey = key; return runtime = (cpu, false, false, "none");
    }

    public static string? FindGpuDevice(string output, string backend, int index)
    {
        string names = backend == "metal" ? $"MTL{index}|Metal{index}" + (index == 0 ? "|Metal" : "") : $"CUDA{index}";
        var match = System.Text.RegularExpressions.Regex.Match(output, $@"(?im)^\s*(?<device>{names}):\s+\S");
        return match.Success ? match.Groups["device"].Value : null;
    }

    public static TranslationResult Parse(string content, TranslationRequest request)
    {
        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            int start = content.IndexOf('\n'), end = content.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) content = content[(start + 1)..end];
        }
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        string? id = root.GetProperty("segmentId").GetString(), text = root.GetProperty("translation").GetString(), language = root.GetProperty("language").GetString();
        if (id != request.SegmentId)
            throw new EngineException("Translation segmentId does not match the requested segmentId.");
        if (string.IsNullOrWhiteSpace(text))
            throw new EngineException("Translation text is empty; return a nonempty translation of the supplied text.");
        if (language != request.TargetLanguage)
            throw new EngineException($"Translation language does not match the requested code '{request.TargetLanguage}'.");
        return new(id, text, language);
    }
    public void Dispose() { host.Dispose(); http.Dispose(); }
}
