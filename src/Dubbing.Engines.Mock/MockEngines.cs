using Dubbing.Core.Engines;

namespace Dubbing.Engines.Mock;

internal static class MockDescriptor
{
    public static EngineDescriptor Create(string id, string name, EngineTask task, EngineCapabilities? capabilities = null) =>
        new("mock", id, name, task, new HashSet<string> { "*" }, capabilities ?? new(), new(), "MIT", "1.0.0");
}

public static class MockEngineCatalog
{
    public static IReadOnlyList<IEngine> Create() => [new MockTranscriptionEngine(), new MockDiarizationEngine(),
        new MockAlignmentEngine(), new MockTranslationEngine(), new MockTtsEngine(),
        new MockTtsEngine("mock/tone-alt", "Alternative tone · demo", 660), new MockSeparationEngine()];
}

public sealed class MockTranscriptionEngine : ITranscriptionEngine
{
    public EngineDescriptor Descriptor { get; } = MockDescriptor.Create("mock/transcription", "Transcription · demo", EngineTask.Transcription);
    public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var duration = request.Duration ?? TimeSpan.FromSeconds(8);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(request));
        string[] texts = ["Hello, welcome to the dubbing studio.", "We can edit every line of the project."];
        var segments = texts.Select((text, index) => new TranscriptSegment(text,
            TimeSpan.FromTicks(duration.Ticks * index / 2),
            TimeSpan.FromTicks(duration.Ticks * (index * 2 + 1) / 4))).ToArray();
        progress?.Report(new(1, "Demo transcription ready"));
        return Task.FromResult(new TranscriptionResult(string.Join(" ", texts), request.Language ?? "en", segments));
    }
}

public sealed class MockDiarizationEngine : IDiarizationEngine
{
    public EngineDescriptor Descriptor { get; } = MockDescriptor.Create("mock/diarization", "Speakers · demo", EngineTask.Diarization);
    public Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var duration = request.Duration ?? TimeSpan.FromSeconds(8);
        var middle = TimeSpan.FromTicks(duration.Ticks / 2);
        progress?.Report(new(1, "Two demonstration speakers"));
        return Task.FromResult(new DiarizationResult([new(TimeSpan.Zero, middle, "speaker_01", 1), new(middle, duration, "speaker_02", 1)]));
    }
}

public sealed class MockTranslationEngine : ITranslationEngine
{
    public EngineDescriptor Descriptor { get; } = MockDescriptor.Create("mock/translation", "Translation · demo", EngineTask.Translation);
    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text = (request.TargetLanguage, request.Text) switch
        {
            ("es", "Hello, welcome to the dubbing studio.") => "Hola, bienvenido al estudio de doblaje.",
            ("es", "We can edit every line of the project.") => "Podemos editar cada frase del proyecto.",
            _ => $"[DEMO {request.TargetLanguage}] {request.Text}"
        };
        return Task.FromResult(new TranslationResult(request.SegmentId, text, request.TargetLanguage));
    }
}

public sealed class MockAlignmentEngine : IAlignmentEngine
{
    public EngineDescriptor Descriptor { get; } = MockDescriptor.Create("mock/alignment", "Alignment · demo", EngineTask.Alignment,
        new() { SupportsWordTimestamps = true });
    public Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var words = request.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = request.Audio.Start ?? TimeSpan.Zero;
        var end = request.Audio.End ?? start + TimeSpan.FromSeconds(words.Length * 0.3);
        return Task.FromResult(new AlignmentResult(words.Select((text, i) => new WordTiming(text,
            start + (end - start) * ((double)i / words.Length),
            start + (end - start) * ((double)(i + 1) / words.Length))).ToArray()));
    }
}

// A transparent passthrough mock, not a claim of actual source separation.
public sealed class MockSeparationEngine : ISeparationEngine
{
    public EngineDescriptor Descriptor { get; } = MockDescriptor.Create("mock/separation", "Simulated separation · demo", EngineTask.Separation);
    public Task<SeparationResult> SeparateAsync(SeparationRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(1, "Demo: se conserva el audio original"));
        return Task.FromResult(new SeparationResult(new Dictionary<string, AudioReference> { ["vocals"] = request.Audio }));
    }
}
