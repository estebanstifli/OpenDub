using Dubbing.Application.Projects;

namespace Dubbing.Application.Tests;

public sealed class ExportedMediaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "opendub-export-links-" + Guid.NewGuid().ToString("N"));
    private string Output(string folder, string file)
    {
        string path = Path.GetFullPath(Path.Combine(directory, folder));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "render-metadata.json"), "{}");
        string output = Path.Combine(path, file); File.WriteAllText(output, "media"); return output;
    }
    [Fact]
    public void FindsPreviousExportAndPrefersVideoOverCompanionWav()
    {
        string video = Output("exports/first", "dubbed.mp4");
        Output("exports/first", "dub_track.wav");
        Assert.Equal(video, ExportedMedia.Find(directory, null));
        string audio = Output("exports/second", "dub_track.mp3");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "exports/second/render-metadata.json"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(audio, ExportedMedia.Find(directory, null));
        Assert.Equal(video, ExportedMedia.Find(directory, Path.GetRelativePath(directory, video)));
    }
    [Fact]
    public void SupportsExternalExportsAndHidesMissingFilesAndTemporaryOutput()
    {
        string audio = Output("external", "dub_track.flac");
        Assert.Equal(audio, ExportedMedia.Find(directory, audio));
        File.Delete(audio);
        Output("renders/export-in-progress", "dubbed.mp4");
        Output("exports/failed.partial-123", "dubbed.mp4");
        Assert.Null(ExportedMedia.Find(directory, audio));
        Assert.Null(ExportedMedia.Find(null, null));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
