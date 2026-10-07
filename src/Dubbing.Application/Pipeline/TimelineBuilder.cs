using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class TimelineBuilder
{
    public IReadOnlyList<DubSegment> Build(TranscriptionResult transcript, DiarizationResult diarization, string targetLanguage)
    {
        var words = transcript.Words?.Count > 0 ? transcript.Words : transcript.Segments.SelectMany(s => s.Words ?? []).ToArray();
        var pieces = new List<(TranscriptSegment Segment, string? Speaker)>();
        if (words.Count > 0)
        {
            var buffer = new List<WordTiming>(); string? speaker = null;
            foreach (var word in words.OrderBy(w => w.Start))
            {
                if (word.End <= word.Start) continue;
                string? nextSpeaker = Speaker(word.Start, word.End, diarization, speaker);
                bool boundary = buffer.Count > 0 && (nextSpeaker != speaker || word.Start - buffer[^1].End > TimeSpan.FromSeconds(0.6)
                    || word.End - buffer[0].Start > TimeSpan.FromSeconds(8) || EndsSentence(buffer[^1].Text));
                if (boundary) Flush();
                speaker = nextSpeaker; buffer.Add(word);
            }
            Flush();
            void Flush()
            {
                if (buffer.Count == 0) return;
                pieces.Add((new(JoinWords(buffer), buffer[0].Start, buffer[^1].End, buffer.ToArray()), speaker)); buffer.Clear();
            }
        }
        else pieces.AddRange(transcript.Segments.Where(s => s.End > s.Start).Select(s => (s, Speaker(s.Start, s.End, diarization, null))));
        return pieces.Select((piece, index) => new DubSegment { Id = $"seg_{index + 1:D6}", Start = piece.Segment.Start,
            End = piece.Segment.End, SpeakerId = piece.Speaker, Source = new(transcript.Language, piece.Segment.Text),
            Translation = new(targetLanguage, ""), Words = piece.Segment.Words ?? [] }).ToArray();
    }
    private static string? Speaker(TimeSpan start, TimeSpan end, DiarizationResult diarization, string? previous) => diarization.Turns
        .Select(turn => new { turn.SpeakerId, turn.Confidence, Overlap = Math.Max(0, Math.Min(turn.End.Ticks, end.Ticks) - Math.Max(turn.Start.Ticks, start.Ticks)) })
        .Where(x => x.Overlap > 0).OrderByDescending(x => x.Overlap).ThenByDescending(x => x.Confidence)
        .ThenByDescending(x => x.SpeakerId == previous).ThenBy(x => x.SpeakerId, StringComparer.Ordinal).FirstOrDefault()?.SpeakerId;
    private static bool EndsSentence(string text) => text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('?') || text.TrimEnd().EndsWith('!') || text.TrimEnd().EndsWith('。');
    private static string JoinWords(IEnumerable<WordTiming> words)
    {
        string result = "";
        foreach (var word in words)
        {
            string text = word.Text.Trim();
            bool separator = result.Length > 0 && text.Length > 0 && !char.IsPunctuation(text[0]) && text[0] < 0x3000 && result[^1] < 0x3000;
            result += (separator ? " " : "") + text;
        }
        return result;
    }
}
