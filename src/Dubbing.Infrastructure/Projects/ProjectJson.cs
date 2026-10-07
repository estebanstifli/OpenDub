using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Dubbing.Core;
using Dubbing.Core.Projects;

namespace Dubbing.Infrastructure.Projects;

public interface IProjectMigration
{
    int FromVersion { get; }
    JsonObject Migrate(JsonObject document);
}

public sealed class ProjectJson(IEnumerable<IProjectMigration>? migrations = null)
{
    private readonly Dictionary<int, IProjectMigration> migrations =
        (migrations ?? []).ToDictionary(x => x.FromVersion);
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string Serialize(DubbingProject project)
    {
        ProjectValidator.Validate(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public DubbingProject Deserialize(string json)
    {
        try
        {
            var document = JsonNode.Parse(json) as JsonObject
                ?? throw new ProjectFormatException("The project must be a JSON object.");
            int version = document["schemaVersion"]?.GetValue<int>()
                ?? throw new ProjectFormatException("Falta schemaVersion.");
            if (version < 1 || version > DubbingProject.CurrentSchemaVersion)
                throw new ProjectFormatException($"Unsupported project version: {version}.");
            while (version < DubbingProject.CurrentSchemaVersion)
            {
                if (!migrations.TryGetValue(version, out var migration))
                    throw new ProjectFormatException($"No migration exists for version {version}.");
                document = migration.Migrate(document);
                if (document["schemaVersion"]?.GetValue<int>() != version + 1)
                    throw new ProjectFormatException("The migration did not advance one version.");
                version++;
            }
            foreach (string field in new[] { "id", "name", "settings", "engines", "speakers", "segments" })
                if (document[field] is null) throw new ProjectFormatException($"Falta el campo obligatorio {field}.");
            var project = document.Deserialize<DubbingProject>(Options)
                ?? throw new ProjectFormatException("Empty project.");
            ProjectValidator.Validate(project);
            return project;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ProjectFormatException("Invalid project file.", ex);
        }
    }
}
