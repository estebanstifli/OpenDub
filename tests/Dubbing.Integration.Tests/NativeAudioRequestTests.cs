using Dubbing.Engines.AudioCpp;

namespace Dubbing.Integration.Tests;

public sealed class NativeAudioRequestTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "audio-path-test-" + Guid.NewGuid().ToString("N"));

    [Fact] public async Task LongUnicodeReferencesBecomeShortNativePathsAndAreRemovedAfterRequest()
    {
        string source = Path.Combine(root, new string('a', 100), new string('b', 100), "diálogo-日本語.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        string cwd = Path.Combine(root, "runtime");
        var original = new { voice_ref = source, audio = source, input = "Hola", language = "es", options = new { duration_factor = .8 } };
        string stagedPath;
        using (var staged = await NativeAudioRequest.CreateAsync(cwd, original, default))
        {
            string relative = staged.Body["voice_ref"]!.GetValue<string>();
            Assert.True(Path.IsPathRooted(relative)); Assert.True(relative.Length < 260);
            Assert.All(relative, c => Assert.InRange((int)c, 0, 127));
            Assert.Equal(relative, staged.Body["audio"]!.GetValue<string>());
            Assert.Equal("Hola", staged.Body["input"]!.GetValue<string>());
            Assert.Equal(.8, staged.Body["options"]!["duration_factor"]!.GetValue<double>());
            stagedPath = Path.Combine(cwd, relative);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(stagedPath));
        }
        Assert.False(File.Exists(stagedPath)); Assert.True(File.Exists(source));
        Assert.Equal(source, original.audio);
        using var nested = await NativeAudioRequest.CreateAsync(cwd, new { request = new { audio = source, text = "words" } }, default);
        Assert.True(File.Exists(Path.Combine(cwd, nested.Body["request"]!["audio"]!.GetValue<string>())));
    }

    [Fact] public async Task FailedOrCancelledPreparationLeavesNoRequestFiles()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.wav"), cwd = Path.Combine(root, "runtime");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await Assert.ThrowsAsync<FileNotFoundException>(() => NativeAudioRequest.CreateAsync(cwd, new { voice_ref = source, audio = source + ".missing" }, default));
        Assert.Empty(Directory.GetDirectories(cwd));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeAudioRequest.CreateAsync(cwd, new { audio = source }, cancelled.Token));
        Assert.Empty(Directory.GetDirectories(cwd)); Assert.True(File.Exists(source));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
