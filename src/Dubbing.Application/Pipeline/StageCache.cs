using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dubbing.Application.Pipeline;

internal static class StageCache
{
    private sealed record Envelope<T>(int SchemaVersion, string Key, T Result);
    public static string Key(params object?[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    public static async Task<T> GetAsync<T>(string directory, string stage, string key, Func<Task<T>> create, CancellationToken ct)
    {
        string file = Path.Combine(directory, "cache", stage + "-" + key + ".json");
        if (File.Exists(file))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope<T>>(await File.ReadAllTextAsync(file, ct));
                if (envelope is { SchemaVersion: 1 } && envelope.Key == key) return envelope.Result;
            }
            catch (JsonException) { }
        }
        T result = await create();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Envelope<T>(1, key, result)), ct);
        File.Move(temporary, file, true);
        return result;
    }
}
