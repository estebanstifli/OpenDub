using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Engines;

public sealed class EngineSelectionService(IEngineRegistry registry)
{
    public IReadOnlyList<EngineDescriptor> Find(EngineTask task, string? language = null,
        Func<EngineCapabilities, bool>? capabilities = null) => registry.GetAvailableEngines(task)
        .Where(x => language is null || x.Languages.Contains("*") || x.Languages.Contains(language))
        .Where(x => capabilities is null || capabilities(x.Capabilities)).ToArray();

    public ITtsEngine ResolveTts(DubbingProject project, DubSegment segment, string? applicationDefault = null)
    {
        var speaker = project.Speakers.FirstOrDefault(x => x.Id == segment.SpeakerId);
        var id = segment.Tts.EngineId ?? speaker?.PreferredTtsEngine ?? project.Engines.Tts ?? applicationDefault
            ?? throw new InvalidOperationException("Select a speech engine.");
        return registry.GetEngine<ITtsEngine>(id);
    }
}
