using System.Text.RegularExpressions;
using Dubbing.Core;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Engines.AudioCpp;

public sealed class AudioRuntimeSelector(IRuntimeLocator locator, IProcessRunner runner)
{
    private readonly Dictionary<string, (string Path, string Backend)> cache = new();
    public async Task<(string Path, string Backend)> ResolveAsync(string executable, string? configured, string mode, int device, CancellationToken ct)
    {
        string key = $"{executable}|{configured}|{mode}|{device}";
        if (cache.TryGetValue(key, out var cached)) return cached;
        string[] candidates = mode == "auto" ? (OperatingSystem.IsMacOS() ? ["metal", "cpu"] : ["cuda", "vulkan", "hip", "cpu"]) : [mode];
        foreach (string backend in candidates)
        {
            try
            {
                string path = locator.LocateAudio(executable, backend, configured);
                if (backend != "cpu")
                {
                    // Metal may compile its embedded shaders during the first probe.
                    var result = await runner.RunAsync(new(path, ["--list-devices"], TimeSpan.FromSeconds(backend == "metal" ? 60 : 20)), ct);
                    if (result.ExitCode != 0 || !HasDevice(result.StandardOutput, backend, device))
                        throw new EngineException($"audio.cpp {backend} device {device} is unavailable. Check the runtime and GPU driver.");
                    ActivityJournal.Write("audio.cpp · devices: " + result.StandardOutput.Trim());
                }
                ActivityJournal.Write($"audio.cpp · requested={mode} · selected={backend} · runtime={path}.");
                return cache[key] = (path, backend);
            }
            catch (Exception ex) when (mode == "auto" && ex is FileNotFoundException or EngineException or TimeoutException or System.ComponentModel.Win32Exception)
            { ActivityJournal.Write($"audio.cpp · {backend} unavailable: {ex.Message}"); }
        }
        throw new EngineException("No usable audio.cpp runtime was found.");
    }
    public static bool HasDevice(string output, string backend, int index)
    {
        // ggml registry names differ from audio.cpp's --backend values.
        string registry = backend.ToLowerInvariant() switch
        {
            "metal" => "(?:MTL|Metal)",
            "hip" => "(?:ROCm|HIP)",
            _ => Regex.Escape(backend)
        };
        return Regex.IsMatch(output, $@"(?im)^\s*{registry}:{index}\s+[^\r\n]*\[(GPU|IGPU)\]\s*$");
    }
}
