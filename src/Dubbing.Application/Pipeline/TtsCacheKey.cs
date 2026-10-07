using System.Security.Cryptography;
using System.Text.Json;
using Dubbing.Core.Engines;

namespace Dubbing.Application.Pipeline;

public static class TtsCacheKey
{
    public static string Create(EngineDescriptor engine, string? modelId, string? modelVersion, TtsRequest request)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            engine.EngineId, engine.Version, ModelId = modelId, ModelVersion = modelVersion,
            request.Text, request.Language, request.SpeakerProfileId, request.VoiceReference,
            request.Emotion, request.TargetDuration, request.SpeakingRate, request.Seed, request.EngineOptions
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, data);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
