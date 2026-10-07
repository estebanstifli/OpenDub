using Dubbing.Core.Engines;
using Dubbing.Core.Media;

namespace Dubbing.Application.Pipeline;

public sealed class ChunkAlignmentService(IMediaEngine media)
{
    public async Task<AlignmentResult> AlignAsync(IAlignmentEngine engine, AudioReference audio,
        TranscriptionResult transcript, string language, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dubbing-align-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var words = new List<WordTiming>();
            for (int i = 0; i < transcript.Segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var segment = transcript.Segments[i];
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                if (segment.End <= segment.Start) throw new InvalidDataException("Invalid transcript chunk duration.");
                var path = Path.Combine(scratch, "chunk.wav");
                await media.CutAsync(audio.Path, path, segment.Start, segment.End - segment.Start, ct);
                progress?.Report(new((double)i / transcript.Segments.Count, $"Aligning words ({i + 1}/{transcript.Segments.Count})…"));
                var result = await engine.AlignAsync(new(new(path), segment.Text, language), ct);
                var tokens = segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int tokenIndex = 0;
                foreach (var word in result.Words)
                {
                    string text = word.Text;
                    // The aligner strips punctuation. Restore it when token identity is exact so
                    // later semantic grouping can keep sentence boundaries without altering timing.
                    string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                    if (tokenIndex < tokens.Length && Key(tokens[tokenIndex]) == Key(word.Text)) text = tokens[tokenIndex++];
                    words.Add(word with { Text = text, Start = word.Start + segment.Start, End = word.End + segment.Start });
                }
            }
            return new(words);
        }
        finally { Directory.Delete(scratch, true); }
    }
}
