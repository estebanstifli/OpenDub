namespace Dubbing.Application.Projects;

public static class ExportedMedia
{
    public static string? Find(string? projectDirectory, string? savedFile)
    {
        if (projectDirectory is null) return null;
        try
        {
            if (!string.IsNullOrWhiteSpace(savedFile))
            {
                string path = Path.GetFullPath(Path.Combine(projectDirectory, savedFile));
                if (File.Exists(path)) return path;
            }
            // Older projects did not record the destination. Look only in known output folders.
            var folders = new List<string> { Path.Combine(projectDirectory, "renders") };
            string exports = Path.Combine(projectDirectory, "exports");
            if (Directory.Exists(exports)) folders.AddRange(Directory.EnumerateDirectories(exports).Where(p => !Path.GetFileName(p).Contains(".partial-")));
            return folders.Where(Directory.Exists)
                .Where(p => File.Exists(Path.Combine(p, "render-metadata.json")))
                .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "render-metadata.json")))
                .Select(p => new[] { "dubbed.mp4", "dubbed.mkv", "dub_track.mp3", "dub_track.flac", "dub_track.m4a", "dub_track.ogg", "dub_track.wav" }
                    .Select(name => Path.Combine(p, name)).FirstOrDefault(File.Exists))
                .FirstOrDefault(p => p is not null);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
