using Dubbing.Core.Engines;
using Dubbing.Engines.AudioCpp;
using Dubbing.Infrastructure.Configuration;
using Dubbing.Infrastructure.Models;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Integration.Tests;

public class SeparationPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagesUnicodeMediaPathsAndCleansUpOnFailure(bool fail)
    {
        string root = Path.Combine(Path.GetTempPath(), "separation-ñ–字幕-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var runner = new RecordingRunner(fail);
        try
        {
            string input = Path.Combine(root, "película – voz.wav"), output = Path.Combine(root, "música");
            await File.WriteAllBytesAsync(input, [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "model.gguf"), [1]);
            string exe = Path.Combine(root, "fake.exe"); await File.WriteAllTextAsync(exe, "fixture");
            var settings = new SettingsStore(Path.Combine(root, "settings.json"));
            await settings.SaveAsync(new() { ModelDirectory = root, AudioCliPath = exe, Backend = "cpu" }, default);
            var model = new CatalogModel { Id = "sep", Provider = "audio.cpp", DisplayName = "Sep", Family = "bs_roformer", Task = EngineTask.Separation,
                EntryPoint = "model.gguf", Files = [new("model.gguf", new Uri("https://example.com/model"), 1, new string('0', 64))] };
            using var coordinator = new RuntimeCoordinator();
            using var transport = new AudioCppTransport(settings, new ModelCatalog(Path.Combine(root, "empty")), new RuntimeLocator(), runner, coordinator);
            if (fail) await Assert.ThrowsAsync<Dubbing.Core.EngineException>(() => transport.SeparateAsync(model, input, output, default));
            else
            {
                await transport.SeparateAsync(model, input, output, default);
                Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(output, "vocals.wav")));
                Assert.True(File.Exists(Path.Combine(output, "instrumental.wav")));
            }
            Assert.NotNull(runner.Scratch); Assert.False(Directory.Exists(runner.Scratch));
            Assert.True(File.Exists(input));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class RecordingRunner(bool fail) : IProcessRunner
    {
        public string? Scratch;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
        {
            if (request.Arguments.Contains("--version")) return Task.FromResult(new ProcessResult(0, "audio.cpp 0.9.0\nbackends: cpu", ""));
            Scratch = request.WorkingDirectory; Assert.NotNull(Scratch);
            var args = request.Arguments.ToList();
            Assert.Equal("input.wav", args[args.IndexOf("--audio") + 1]);
            Assert.Equal("stems", args[args.IndexOf("--out-dir") + 1]);
            Assert.True(File.Exists(Path.Combine(Scratch, "input.wav")));
            if (fail) return Task.FromResult(new ProcessResult(1, "", "fixture failure"));
            Directory.CreateDirectory(Path.Combine(Scratch, "stems"));
            foreach (string name in new[] { "vocals.wav", "instrumental.wav" }) File.Copy(Path.Combine(Scratch, "input.wav"), Path.Combine(Scratch, "stems", name));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
