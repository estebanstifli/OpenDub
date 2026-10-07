using System.Text.Json;
using System.Runtime.InteropServices;
using Dubbing.Infrastructure.Processes;
using Dubbing.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.App.Services;

public static class PackageSelfCheck
{
    [DllImport("libSystem.B.dylib", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetNativeEnvironment(string name, string value, int overwrite);

    public static string? VlcDirectory()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "VLC.app", "Contents", "MacOS", "lib"));
        string library = Directory.Exists(bundled) ? bundled : "/Applications/VLC.app/Contents/MacOS/lib";
        string plugins = Path.GetFullPath(Path.Combine(library, "..", "plugins"));
        if (Directory.Exists(plugins))
        {
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", plugins);
            // libvlc reads libc's environment; .NET's Unix environment is a separate managed copy.
            if (SetNativeEnvironment("VLC_PLUGIN_PATH", plugins, 1) != 0)
                throw new InvalidOperationException("Could not configure the bundled VLC plugins.");
        }
        return library;
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
            using var vlc = new LibVLCSharp.Shared.LibVLC("--no-video-title-show", "--verbose=2");
            var catalog = new ModelCatalog(Path.Combine(AppContext.BaseDirectory, "engine-catalog"));
            if (catalog.Models.Count != 6) throw new InvalidDataException("Incomplete model catalog.");
            Console.WriteLine(JsonSerializer.Serialize(new { product = "OpenDub", version = "0.1.0", models = catalog.Models.Count, vlc = vlc.Version, status = "ok" }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
