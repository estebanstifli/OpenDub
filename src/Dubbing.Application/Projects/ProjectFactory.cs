using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Projects;

public sealed class ProjectFactory(IEngineRegistry registry)
{
    public DubbingProject Create(string? provider = null) => new()
    {
        Settings = new() { UseAlignment = First(EngineTask.Alignment, provider)?.StartsWith("audio.cpp/", StringComparison.Ordinal) == true },
        Engines = new()
        {
            Transcription = First(EngineTask.Transcription, provider), Diarization = First(EngineTask.Diarization, provider),
            Alignment = First(EngineTask.Alignment, provider), Translation = First(EngineTask.Translation, provider),
            Tts = First(EngineTask.Tts, provider), Separation = First(EngineTask.Separation, provider)
        }
    };
    private string? First(EngineTask task, string? provider) => registry.GetAvailableEngines(task)
        .Where(e => provider is null || e.ProviderId == provider)
        .OrderByDescending(e => task == EngineTask.Translation && e.ProviderId == "llama.cpp").FirstOrDefault()?.EngineId;
}
