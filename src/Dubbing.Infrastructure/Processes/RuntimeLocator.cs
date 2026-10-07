using System.Runtime.InteropServices;

namespace Dubbing.Infrastructure.Processes;

public interface IRuntimeLocator { string Locate(string executable, string? configuredPath = null);
    string LocateLlamaCuda(string? configuredPath = null) => Locate("llama-server", configuredPath); }

public sealed class RuntimeLocator(string? baseDirectory = null) : IRuntimeLocator
{
    public string LocateLlamaCuda(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return Locate("llama-server", configuredPath);
        string folder = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "llama-cuda");
        string file = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, file, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("llama.cpp CUDA runtime is not installed.") : throw new FileNotFoundException("llama.cpp CUDA runtime is not installed.");
    }
    public string Locate(string executable, string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);
            throw new FileNotFoundException($"Configured executable not found: {executable}.", configuredPath);
        }
        string fileName = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
        var bundled = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, fileName);
        if (File.Exists(bundled)) return bundled;
        foreach (string folder in new[] { "audio", "llama", "ffmpeg" })
        {
            var nested = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, folder, fileName);
            if (File.Exists(nested)) return nested;
            string component = Path.GetDirectoryName(nested)!;
            if (Directory.Exists(component))
            {
                var located = Directory.EnumerateFiles(component, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (located is not null) return located;
            }
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim('"'), fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"{executable} is not installed. Configure its path or add it to PATH.");
    }
}
