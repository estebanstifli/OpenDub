using System.Text.Json;
using Dubbing.Infrastructure.Processes;
using Dubbing.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.App.Services;

public static class PackageSelfCheck
{
    public static string? VlcDirectory()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "VLC.app", "Contents", "MacOS", "lib"));
        return Directory.Exists(bundled) ? bundled : "/Applications/VLC.app/Contents/MacOS/lib";
    }
    public static async Task<int> RunAsync()
    {
        try
        {
            var locator = new RuntimeLocator();
            var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
            foreach (string name in new[] { "audiocpp_server", "audiocpp_cli", "llama-server", "ffmpeg", "ffprobe" })
            {
                var result = await runner.RunAsync(new(locator.Locate(name), [name.StartsWith("ff") ? "-version" : "--version"], TimeSpan.FromSeconds(30)), default);
                if (result.ExitCode != 0) throw new InvalidOperationException(name + ": " + result.StandardError);
                Console.WriteLine(name + " OK");
            }
            LibVLCSharp.Shared.Core.Initialize(VlcDirectory());
            using var vlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show");
            var catalog = new ModelCatalog(Path.Combine(AppContext.BaseDirectory, "engine-catalog"));
            if (catalog.Models.Count != 6) throw new InvalidDataException("Incomplete model catalog.");
            Console.WriteLine(JsonSerializer.Serialize(new { product = "OpenDub", version = "0.1.0", models = catalog.Models.Count, vlc = vlc.Version, status = "ok" }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
