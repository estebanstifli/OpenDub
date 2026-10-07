using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

// Candidates never become project assets until all fitting phases for their segment finish.
internal sealed class SpeechWorkspace : IDisposable
{
    private readonly string root, path;
    public string RelativeDirectory { get; } = "cache/tts-work/" + Guid.NewGuid().ToString("N");
    public SpeechWorkspace(string directory)
    {
        root = Path.GetFullPath(directory); path = Path.GetFullPath(Path.Combine(root, RelativeDirectory));
        Directory.CreateDirectory(path);
    }
    public GeneratedSpeech Publish(GeneratedSpeech speech)
    {
        string source = Path.GetFullPath(Path.Combine(root, speech.File));
        if (!source.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return speech;
        string relative = $"generated/tts/{speech.CacheKey}-{Guid.NewGuid():N}.wav";
        string destination = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        return speech with { File = relative };
    }
    public void Dispose()
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
