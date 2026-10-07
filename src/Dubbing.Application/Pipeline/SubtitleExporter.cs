using System.Text;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public static class SubtitleExporter
{
    public static Task WriteSrtAsync(DubbingProject project, string output, CancellationToken ct)
    {
        var text = new StringBuilder(); int number = 0;
        foreach (var segment in project.Segments.Where(s => !s.ExcludedFromDubbing).OrderBy(s => s.Start))
        {
            text.AppendLine((++number).ToString()).AppendLine($"{Format(segment.Start)} --> {Format(segment.End)}")
                .AppendLine(segment.Translation.Text.Replace("\r", "").Trim()).AppendLine();
        }
        return File.WriteAllTextAsync(output, text.ToString(), new UTF8Encoding(false), ct);
    }
    private static string Format(TimeSpan value) => $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2},{value.Milliseconds:D3}";
}
