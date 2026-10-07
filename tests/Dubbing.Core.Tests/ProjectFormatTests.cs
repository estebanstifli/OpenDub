using Dubbing.Core.Engines;
using Dubbing.Core.Projects;
using Dubbing.Infrastructure.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Core.Tests;

public sealed class ProjectFormatTests
{
    private readonly ProjectJson json = new();
    private static DubbingProject Example() => new()
    {
        Name = "Película 日本語", Engines = new() { Tts = "future/voice" },
        Speakers = [new() { Id = "a", VoiceReference = new("assets/voices/a.wav", "abc") }],
        Settings = new() { Glossary = new Dictionary<string, string> { ["Starfleet"] = "Flota Estelar" } },
        Segments = [new() { Id = "s1", Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3), SpeakerId = "a",
            Translation = new("es", "¡Hola!", true), Emotion = new() { Mode = EmotionMode.Label, Label = "happy", Intensity = 0.75 },
            Tts = new() { EngineOptions = new Dictionary<string, object?> { ["temperature"] = 0.7 } } },
            new() { Id = "s2", Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4) }]
    };

    [Fact] public void RoundTripPreservesEditableProjectAndOverlaps()
    {
        var project = Example();
        var restored = json.Deserialize(json.Serialize(project));
        Assert.Equal(json.Serialize(project), json.Serialize(restored));
        Assert.True(restored.Segments[0].Translation.Locked);
        Assert.True(restored.Segments[1].Start < restored.Segments[0].End);
        Assert.Equal("future/voice", restored.Engines.Tts);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":999}")]
    [InlineData("{\"schemaVersion\":0}")]
    [InlineData("{\"schemaVersion\":1,\"segments\":null}")]
    [InlineData("not json")]
    public void RejectsMalformedAndUnknownVersions(string input) => Assert.Throws<ProjectFormatException>(() => json.Deserialize(input));

    [Fact] public void RejectsDuplicateIdsAndTraversal()
    {
        var project = Example();
        Assert.Throws<ProjectFormatException>(() => json.Serialize(project with { Segments = [project.Segments[0], project.Segments[0]] }));
        Assert.Throws<ProjectFormatException>(() => json.Serialize(project with { Segments = [project.Segments[0] with
            { GeneratedSpeech = new("../outside.wav", TimeSpan.FromSeconds(2), "key") }] }));
    }

    [Fact] public async Task AtomicSaveKeepsPreviousBackupAndLeavesNoTemps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dubbing-tests-" + Guid.NewGuid());
        try
        {
            var store = new JsonProjectStore(json, NullLogger<JsonProjectStore>.Instance);
            var project = Example();
            await store.SaveAsync(project, directory, TestContext.Token);
            await store.SaveAsync(project with { Name = "Updated" }, directory, TestContext.Token);
            Assert.Equal("Updated", (await store.LoadAsync(directory, TestContext.Token)).Name);
            Assert.Equal(project.Name, json.Deserialize(await File.ReadAllTextAsync(Path.Combine(directory, "project.json.bak"))).Name);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(project, directory, canceled.Token));
            Assert.Equal("Updated", (await store.LoadAsync(directory, TestContext.Token)).Name);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

// xUnit v2 has no test cancellation token; do not download or run model workloads in these tests.
internal static class TestContext { public static CancellationToken Token => CancellationToken.None; }
