using System.Text.Json;
using Dubbing.Infrastructure.Configuration;

namespace Dubbing.Infrastructure.Projects;

public sealed class RecentProjectStore(string? filePath = null)
{
    private readonly string file = filePath ?? Path.Combine(AppPaths.DataRoot, "recent-projects.json");
    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public IReadOnlyList<string> Read()
    {
        try { return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? []).Select(AppPaths.Resolve).Distinct(Comparer).Take(10).ToArray(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
    public async Task RecordAsync(string directory, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        var items = new[] { directory }.Concat(Read()).Distinct(Comparer).Take(10);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(items.Select(p => AppPaths.IsPortable ? AppPaths.StorePath(p) : p)), ct);
        File.Move(temp, file, true);
    }
}
