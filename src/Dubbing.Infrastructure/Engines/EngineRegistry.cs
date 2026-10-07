using Dubbing.Core.Engines;
using Microsoft.Extensions.DependencyInjection;

namespace Dubbing.Infrastructure.Engines;

public interface IEnginePlugin
{
    string PluginId { get; }
    void Register(IServiceCollection services);
}

public sealed class EngineRegistry : IEngineRegistry
{
    private readonly IReadOnlyDictionary<string, IEngine> engines;
    public EngineRegistry(IEnumerable<IEngine> engines)
    {
        var entries = new Dictionary<string, IEngine>(StringComparer.Ordinal);
        foreach (var engine in engines)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(engine.Descriptor.EngineId);
            if (!MatchesContract(engine))
                throw new ArgumentException($"Engine '{engine.Descriptor.EngineId}' does not implement its declared task.");
            if (!entries.TryAdd(engine.Descriptor.EngineId, engine))
                throw new ArgumentException($"Duplicate engine ID '{engine.Descriptor.EngineId}'.");
        }
        this.engines = entries;
    }

    public IReadOnlyList<EngineDescriptor> GetAvailableEngines(EngineTask task) => engines.Values
        .Select(x => x.Descriptor).Where(x => x.Task == task).OrderBy(x => x.EngineId, StringComparer.Ordinal).ToArray();

    public T GetEngine<T>(string engineId) where T : class, IEngine
    {
        if (!engines.TryGetValue(engineId, out var engine))
            throw new KeyNotFoundException($"Engine unavailable: {engineId}.");
        return engine as T ?? throw new InvalidOperationException($"Engine {engineId} does not implement {typeof(T).Name}.");
    }

    private static bool MatchesContract(IEngine engine) => engine.Descriptor.Task switch
    {
        EngineTask.Transcription => engine is ITranscriptionEngine,
        EngineTask.Diarization => engine is IDiarizationEngine,
        EngineTask.Alignment => engine is IAlignmentEngine,
        EngineTask.Translation => engine is ITranslationEngine,
        EngineTask.Tts => engine is ITtsEngine,
        EngineTask.Separation => engine is ISeparationEngine,
        _ => false
    };
}
