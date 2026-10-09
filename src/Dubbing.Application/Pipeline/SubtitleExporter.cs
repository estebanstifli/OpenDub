using System.Text;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public static class SubtitleExporter
{
    public static Task WriteSrtAsync(DubbingProject project, string output, CancellationToken ct, TimeSpan? start = null, TimeSpan? end = null)
    {
        var text = new StringBuilder(); int number = 0;
        foreach (var segment in project.Segments.Where(s => !s.ExcludedFromDubbing).OrderBy(s => s.Start))
        {
            var begin = start ?? TimeSpan.Zero; var finish = end ?? TimeSpan.MaxValue;
            if (segment.End <= begin || segment.Start >= finish) continue;
            var from = (segment.Start < begin ? begin : segment.Start) - begin;
            var to = (segment.End > finish ? finish : segment.End) - begin;
            text.AppendLine((++number).ToString()).AppendLine($"{Format(from)} --> {Format(to)}")
                .AppendLine(segment.Translation.Text.Replace("\r", "").Trim()).AppendLine();
        }
        return File.WriteAllTextAsync(output, text.ToString(), new UTF8Encoding(false), ct);
    }
    private static string Format(TimeSpan value) => $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2},{value.Milliseconds:D3}";
}
