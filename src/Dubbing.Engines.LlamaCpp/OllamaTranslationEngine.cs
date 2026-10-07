using System.Net.Http.Json;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Engines.LlamaCpp;

public sealed class OllamaTranslationEngine : ITranslationEngine, IDisposable
{
    public const string Id = "ollama/local-translator";
    private readonly HttpClient http;
    private readonly RuntimeCoordinator coordinator;
    private readonly HashSet<string> thinkingModels = [];
    private string? residentModel;
    private IReadOnlyList<string>? availableModels;
    public OllamaTranslationEngine(RuntimeCoordinator coordinator, HttpMessageHandler? handler = null)
    {
        this.coordinator = coordinator;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri("http://127.0.0.1:11434/");
        http.Timeout = TimeSpan.FromMinutes(10);
    }
    public EngineDescriptor Descriptor { get; } = new("ollama", Id, "Ollama (local)", EngineTask.Translation,
        new HashSet<string> { "*" }, new(), new(), "MIT runtime; model license varies", "1");

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var root = await http.GetFromJsonAsync<JsonElement>("api/tags", timeout.Token);
            var names = new List<string>();
            foreach (var model in root.GetProperty("models").EnumerateArray())
            {
                string name = model.GetProperty("name").GetString()!;
                if (name.Contains(":cloud", StringComparison.OrdinalIgnoreCase) || IsRemote(model)) continue;
                using var response = await http.PostAsJsonAsync("api/show", new { model = name }, timeout.Token);
                await CheckAsync(response, timeout.Token);
                var details = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                if (IsRemote(details)) continue;
                if (details.TryGetProperty("capabilities", out var capabilities) &&
                    !capabilities.EnumerateArray().Any(c => c.GetString() == "completion")) continue;
                names.Add(name);
                if (details.TryGetProperty("capabilities", out var caps) && caps.EnumerateArray().Any(c => c.GetString() == "thinking")) thinkingModels.Add(name);
            }
            return availableModels = names.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (HttpRequestException ex) { throw new EngineException("Cannot connect to local Ollama. Start Ollama and refresh the model list.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new EngineException("Local Ollama did not respond in time. Refresh the model list.", ex); }
    }
    private static bool IsRemote(JsonElement model) => model.TryGetProperty("remote_host", out var host) && !string.IsNullOrWhiteSpace(host.GetString());

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct) =>
        coordinator.RunAsync("ollama", ReleaseAsync, () => { }, async () =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelId)) throw new EngineException("Select an Ollama model in Engines before translating.");
            if (!(availableModels ?? await ListModelsAsync(ct)).Contains(request.ModelId)) throw new ModelNotInstalledException("The selected local Ollama model is unavailable. Refresh the model list in Engines.");
            if (residentModel != request.ModelId)
            {
                await ReleaseAsync(); residentModel = request.ModelId;
                ActivityJournal.Write($"Ollama · loading {residentModel}; keeping it resident between operations until released.");
            }
            else ActivityJournal.Write($"Ollama · reusing {residentModel}.");
            var payload = JsonSerializer.Serialize(new { segmentId = request.SegmentId, text = request.Text,
                sourceLanguage = request.SourceLanguage, targetLanguage = request.TargetLanguage,
                previous = request.PreviousSegments, next = request.NextSegments, speaker = request.SpeakerId,
                targetSeconds = request.TargetDuration?.TotalSeconds, glossary = request.Glossary, style = request.StyleInstructions });
            string system = TranslationPrompts.Build(request.Prompt);
            Exception? last = null;
            var messages = new List<object> { new { role = "system", content = system }, new { role = "user", content = payload } };
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using var response = await http.PostAsJsonAsync("api/chat", new
                {
                    model = request.ModelId, stream = false, keep_alive = -1,
                    think = thinkingModels.Contains(request.ModelId) ? (bool?)false : null,
                    options = new { temperature = 0.2, num_predict = 2048, num_ctx = 8192 },
                    messages,
                    format = new { type = "object", properties = new { segmentId = new { type = "string", @enum = new[] { request.SegmentId } }, translation = new { type = "string", minLength = 1 }, language = new { type = "string", @enum = new[] { request.TargetLanguage } } },
                        required = new[] { "segmentId", "translation", "language" }, additionalProperties = false }
                }, ct);
                await CheckAsync(response, ct);
                string? content = null;
                try
                {
                    var root = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    content = root.GetProperty("message").GetProperty("content").GetString();
                    if (string.IsNullOrWhiteSpace(content)) throw new EngineException("Ollama returned empty message content.");
                    if (root.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length")
                        throw new EngineException("Ollama response reached the output token limit; return only the concise translation JSON.");
                    return LlamaCppTranslationEngine.Parse(content, request);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or EngineException)
                {
                    last = ex;
                    ActivityJournal.Write($"Ollama · segment {request.SegmentId} · invalid response {attempt + 1}/3 · {ex.Message}");
                    if (content is not null) messages.Add(new { role = "assistant", content = content[..Math.Min(content.Length, 4096)] });
                    messages.Add(new { role = "user", content = $"Your response failed validation: {ex.Message} Return a corrected JSON object only. Copy segmentId exactly: {JsonSerializer.Serialize(request.SegmentId)}. Use language exactly: {JsonSerializer.Serialize(request.TargetLanguage)}. Translate the original text, not the surrounding context. translation must not be empty." });
                }
            }
            throw new EngineException($"Ollama could not translate segment {request.SegmentId} after three attempts: {last?.Message}", last);
        }, ct);
    private async Task ReleaseAsync()
    {
        if (residentModel is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await http.PostAsJsonAsync("api/generate", new { model = residentModel, keep_alive = 0 }, timeout.Token);
        await CheckAsync(response, timeout.Token);
        ActivityJournal.Write($"Ollama · unloaded {residentModel}."); residentModel = null;
    }
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(ct);
        throw new EngineException($"Ollama returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}");
    }
    public void Dispose() => http.Dispose();
}
