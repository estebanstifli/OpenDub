using System.Text.Json;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class SegmentNormalizer
{
    public IReadOnlyList<DubSegment> Normalize(IReadOnlyList<DubSegment> segments, bool paragraphs = false)
    {
        var output = new List<DubSegment>();
        var group = new List<DubSegment>();
        double maximum = paragraphs ? 30 : 18;
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (group.Count > 0)
            {
                var last = group[^1];
                bool compatible = segment.SpeakerId is not null && segment.SpeakerId == last.SpeakerId &&
                    segment.Start >= last.End && (segment.Start - last.End).TotalSeconds <= (paragraphs ? 3 : 2.5) &&
                    (segment.End - group[0].Start).TotalSeconds <= maximum &&
                    !segment.ExcludedFromDubbing && !last.ExcludedFromDubbing &&
                    !segment.Translation.Locked && !last.Translation.Locked &&
                    segment.GeneratedSpeech is null && last.GeneratedSpeech is null &&
                    segment.FittedSpeech is null && last.FittedSpeech is null &&
                    segment.Source.Language == last.Source.Language &&
                    JsonSerializer.Serialize(segment.Emotion) == JsonSerializer.Serialize(last.Emotion) &&
                    JsonSerializer.Serialize(segment.Tts) == JsonSerializer.Serialize(last.Tts);
                bool sentenceEnd = !paragraphs && (last.End - group[0].Start).TotalSeconds >= 3 &&
                    last.Source.Text.TrimEnd().EndsWithAnySentenceMark();
                if (!compatible || sentenceEnd) Flush();
            }
            group.Add(segment);
        }
        Flush();
        return output;

        void Flush()
        {
            if (group.Count == 0) return;
            if (group.Count == 1) output.Add(group[0]);
            else
            {
                var first = group[0];
                output.Add(first with { Id = Guid.NewGuid().ToString("N"), End = group[^1].End,
                    Source = first.Source with { Text = string.Join(" ", group.Select(s => s.Source.Text.Trim())) },
                    Translation = new(first.Translation.Language, ""), Words = group.SelectMany(s => s.Words).ToArray(),
                    GeneratedSpeech = null, FittedSpeech = null, Status = SegmentStatus.Analyzed,
                    DirtyStages = new[] { PipelineStage.Translation }.Concat(StageDependencies.After(PipelineStage.Translation)).Distinct().Order().ToArray() });
            }
            group.Clear();
        }
    }
}

internal static class SentenceMarks
{
    public static bool EndsWithAnySentenceMark(this string text) => text.TrimEnd('"', '\'', '”', '»').EndsWithAny(['.', '!', '?', '。', '！', '？']);
    private static bool EndsWithAny(this string text, char[] marks) => text.Length > 0 && marks.Contains(text[^1]);
}
