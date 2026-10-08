using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Dubbing.Core;

[assembly: InternalsVisibleTo("Dubbing.Integration.Tests")]

namespace Dubbing.Engines.AudioCpp;

// Native WAV readers use narrow paths on Windows. Keep all media arguments short
// and ASCII. The server resolves relative paths against its executable folder.
internal sealed class NativeAudioRequest : IDisposable
{
    private readonly string directory;
    private readonly string relativeDirectory = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, string> copies = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public JsonNode Body { get; }

    private NativeAudioRequest(string workingDirectory, object request)
    {
        directory = Path.Combine(workingDirectory, relativeDirectory);
        Body = JsonSerializer.SerializeToNode(request)!;
    }

    public static async Task<NativeAudioRequest> CreateAsync(string workingDirectory, object request, CancellationToken ct)
    {
        var staged = new NativeAudioRequest(workingDirectory, request);
        try { await staged.VisitAsync(staged.Body, ct); return staged; }
        catch { staged.Dispose(); throw; }
    }

    private async Task VisitAsync(JsonNode node, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (node is JsonObject obj)
            foreach (var property in obj.ToArray())
            {
                if (property.Key is "audio" or "voice_ref" && property.Value is JsonValue value && value.TryGetValue<string>(out var path))
                {
                    string fullPath = Path.GetFullPath(path);
                    if (!copies.TryGetValue(fullPath, out string? relative))
                    {
                        Directory.CreateDirectory(directory);
                        relative = relativeDirectory + "/" + copies.Count + ".wav";
                        await using var source = File.OpenRead(fullPath);
                        await using var target = File.Create(Path.Combine(directory, copies.Count + ".wav"));
                        await source.CopyToAsync(target, ct);
                        copies.Add(fullPath, relative);
                    }
                    obj[property.Key] = NativePath(Path.Combine(directory, Path.GetFileName(relative)));
                }
                else if (property.Value is { } child) await VisitAsync(child, ct);
            }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) await VisitAsync(child, ct);
    }

    private static string NativePath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.Length < 260 && path.All(c => c < 128)) return path;
        var buffer = new StringBuilder(32768);
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        string shortened = buffer.ToString();
        if (length > 0 && length < buffer.Capacity && shortened.Length < 260 && shortened.All(c => c < 128)) return shortened;
        throw new EngineException("The Windows audio runtime needs a short ASCII temporary path. Set TEMP to a writable folder such as C:\\OpenDubTemp and restart OpenDub.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortPath, uint bufferLength);

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
