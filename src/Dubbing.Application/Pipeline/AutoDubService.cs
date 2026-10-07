using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class AutoDubService(ImportVideoService import, TranslationService translate, GenerateSpeechService speech, ExportVideoService export, IProjectStore store)
{
    public async Task<DubbingProject> RunAsync(DubbingProject project, string input, string directory, ExportOptions output,
        IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        IProgress<EngineProgress> Stage(int stage, string name) => new StageProgress(stage, name, progress);
        // Import caches its analysis stages. Translation and speech save a checkpoint after each segment.
        if (project.Segments.Count == 0)
            project = await import.ImportAsync(project, input, directory, Stage(0, "Analyze"), ct);
        else progress?.Report(new(.25, "Auto dub · resuming existing analysis"));
        if (!project.Segments.Any(s => !s.ExcludedFromDubbing && !string.IsNullOrWhiteSpace(s.Source.Text)))
            throw new InvalidOperationException("No dialogue to dub. Review the selected audio track and detected segments.");
        project = await translate.TranslateAsync(project, Stage(1, "Translate"), ct, p => store.SaveAsync(p, directory, ct));
        await store.SaveAsync(project, directory, ct);
        project = await speech.GenerateAsync(project, directory, Stage(2, "Generate and fit"), ct);
        await store.SaveAsync(project, directory, ct);
        project = await export.ExportAsync(project, directory, Stage(3, "Export"), ct, output);
        progress?.Report(new(1, "Auto dub complete."));
        return project;
    }
    private sealed class StageProgress(int stage, string name, IProgress<EngineProgress>? output) : IProgress<EngineProgress>
    {
        public void Report(EngineProgress value) => output?.Report(value with { Fraction = (stage + Math.Clamp(value.Fraction, 0, 1)) / 4, Message = $"Auto dub · {name} · {value.Message}" });
    }
}
