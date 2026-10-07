using System.Text;
using Dubbing.Core.Projects;
using Microsoft.Extensions.Logging;

namespace Dubbing.Infrastructure.Projects;

public sealed class JsonProjectStore(ProjectJson json, ILogger<JsonProjectStore> logger) : IProjectStore
{
    // One writer in this application; unique temp names also protect interrupted writes.
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public async Task<DubbingProject> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(Path.Combine(directory, "project.json"), cancellationToken);
        var project = json.Deserialize(content);
        return project.Source is { } source && !Path.IsPathRooted(source.Path)
            ? project with { Source = source with { Path = Path.GetFullPath(Path.Combine(directory, source.Path)) } } : project;
    }

    public async Task SaveAsync(DubbingProject project, string directory, CancellationToken cancellationToken)
    {
        var stored = project;
        if (project.Source is { } source)
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(source.Path));
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                stored = project with { Source = source with { Path = relative.Replace('\\', '/') } };
        }
        var content = Encoding.UTF8.GetBytes(json.Serialize(stored));
        await writeGate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            foreach (var child in new[] { "assets/voices", "assets/extracted", "assets/stems", "generated/tts", "generated/previews", "cache", "renders" })
                Directory.CreateDirectory(Path.Combine(directory, child));
            var destination = Path.Combine(directory, "project.json");
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temporary, destination, destination + ".bak");
            else File.Move(temporary, destination);
            logger.LogInformation("Saved project {ProjectId}", project.Id);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            writeGate.Release();
        }
    }
}
