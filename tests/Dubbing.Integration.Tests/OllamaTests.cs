using System.Net;
using System.Text;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Engines.LlamaCpp;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Integration.Tests;

public sealed class OllamaTests
{
    [Fact] public async Task ListsLocalChatModelsAndValidatesTranslationRetries()
    {
        using var coordinator = new RuntimeCoordinator();
        var handler = new Handler();
        using var engine = new OllamaTranslationEngine(coordinator, handler);
        Assert.Equal(new[] { "test:8b" }, await engine.ListModelsAsync(default));
        var result = await engine.TranslateAsync(new("s1", "Hello", "en", "es") { ModelId = "test:8b", Prompt = "Use Spanish from Spain.", Glossary = new Dictionary<string, string> { ["Hello"] = "Hola" } }, default);
        Assert.Contains("Spanish from Spain", handler.Chat.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("Return JSON", handler.Chat.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Hola", result.Text);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal("s1", handler.Chat.GetProperty("format").GetProperty("properties").GetProperty("segmentId").GetProperty("enum")[0].GetString());
        Assert.Equal("es", handler.Chat.GetProperty("format").GetProperty("properties").GetProperty("language").GetProperty("enum")[0].GetString());
        Assert.Contains("failed validation", handler.Chat.GetProperty("messages")[3].GetProperty("content").GetString());
        Assert.Equal("test:8b", handler.Chat.GetProperty("model").GetString());
        Assert.False(handler.Chat.GetProperty("stream").GetBoolean());
        Assert.False(handler.Chat.GetProperty("think").GetBoolean());
        Assert.Equal(-1, handler.Chat.GetProperty("keep_alive").GetInt32());
        await engine.TranslateAsync(new("s1", "Hello again", "en", "es") { ModelId = "test:8b" }, default);
        Assert.Null(handler.UnloadedModel);
        Assert.Contains("glossary", handler.Chat.GetProperty("messages")[1].GetProperty("content").GetString());
        await coordinator.ReleaseAsync();
        Assert.Equal("test:8b", handler.UnloadedModel);
        await Assert.ThrowsAsync<ModelNotInstalledException>(() => engine.TranslateAsync(new("s1", "Hello", "en", "es") { ModelId = "missing" }, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranslateAsync(new("s1", "Hello", "en", "es") { ModelId = "test:8b" }, cancellation.Token));
    }

    [Theory]
    [InlineData("language")]
    [InlineData("empty")]
    [InlineData("json")]
    public async Task RetriesWithFeedbackAndStillRejectsPersistentlyInvalidResponses(string failure)
    {
        using var coordinator = new RuntimeCoordinator();
        var handler = new Handler(failure, true);
        using var engine = new OllamaTranslationEngine(coordinator, handler);
        var error = await Assert.ThrowsAsync<EngineException>(() => engine.TranslateAsync(new("s1", "Hello", "en", "es") { ModelId = "test:8b" }, default));
        Assert.Equal(3, handler.Attempts);
        Assert.Contains("segment s1", error.Message);
        Assert.Equal(6, handler.Chat.GetProperty("messages").GetArrayLength());
        await coordinator.ReleaseAsync();
    }

    [LocalOllamaFact] public async Task InstalledOllamaTranslatesEnglishIntoSpanish()
    {
        using var coordinator = new RuntimeCoordinator();
        using var engine = new OllamaTranslationEngine(coordinator);
        string model = Environment.GetEnvironmentVariable("DUBBING_OLLAMA_TEST_MODEL")!;
        Assert.Contains(model, await engine.ListModelsAsync(default));
        try
        {
            const string id = "026e6641ca1040c18d4fe0d280509744";
            var result = await engine.TranslateAsync(new(id, "I miss you. Please come home.", "en", "es") { ModelId = model }, default);
            Assert.Equal(id, result.SegmentId); Assert.Equal("es", result.Language); Assert.NotEmpty(result.Text);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ollama-smoke.json"), JsonSerializer.Serialize(result));
        }
        finally { await coordinator.ReleaseAsync(); }
    }
    private sealed class LocalOllamaFactAttribute : FactAttribute
    {
        public LocalOllamaFactAttribute() { if (Environment.GetEnvironmentVariable("DUBBING_OLLAMA_TEST_MODEL") is null) Skip = "Requires an explicitly selected local Ollama model."; }
    }
    internal sealed class Handler(string failure = "id", bool alwaysInvalid = false) : HttpMessageHandler
    {
        public int Attempts;
        public JsonElement Chat;
        public string? UnloadedModel;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string response;
            if (request.RequestUri!.AbsolutePath == "/api/tags") response = """{"models":[{"name":"test:8b"},{"name":"embed"},{"name":"remote:cloud"},{"name":"remote-alias"}]}""";
            else
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                if (request.RequestUri.AbsolutePath == "/api/show") response = body.GetProperty("model").GetString() switch
                { "embed" => """{"capabilities":["embedding"]}""", "remote-alias" => """{"remote_host":"https://ollama.com","capabilities":["completion"]}""", _ => """{"capabilities":["completion","thinking"]}""" };
                else if (request.RequestUri.AbsolutePath == "/api/generate")
                { UnloadedModel = body.GetProperty("model").GetString(); Assert.Equal(0, body.GetProperty("keep_alive").GetInt32()); response = "{}"; }
                else
                {
                    Chat = body; Attempts++;
                    bool invalid = alwaysInvalid || Attempts == 1;
                    string content = invalid && failure == "json" ? "{broken" : JsonSerializer.Serialize(new {
                        segmentId = invalid && failure == "id" ? "wrong" : "s1",
                        translation = invalid && failure == "empty" ? " " : "Hola",
                        language = invalid && failure == "language" ? "English" : "es" });
                    response = JsonSerializer.Serialize(new { message = new { content } });
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
