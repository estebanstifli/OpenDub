using Dubbing.Core.Engines;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;

namespace Dubbing.Core.Tests;

public sealed class RegistryTests
{
    [Fact] public void ResolvesTwoTtsImplementationsByConfiguration()
    {
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        Assert.Equal(2, registry.GetAvailableEngines(EngineTask.Tts).Count);
        Assert.NotSame(registry.GetEngine<ITtsEngine>("mock/tone"), registry.GetEngine<ITtsEngine>("mock/tone-alt"));
        Assert.All(registry.GetAvailableEngines(EngineTask.Transcription), e => Assert.Equal(EngineTask.Transcription, e.Task));
    }
    [Fact] public void RejectsDuplicateIds() => Assert.Throws<ArgumentException>(() =>
        new EngineRegistry([new MockTtsEngine(), new MockTtsEngine()]));
    [Fact] public void FailsClearlyForMissingEngineAndWrongContract()
    {
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        Assert.Throws<KeyNotFoundException>(() => registry.GetEngine<ITtsEngine>("missing"));
        Assert.Throws<InvalidOperationException>(() => registry.GetEngine<ITranscriptionEngine>("mock/tone"));
    }
    [Fact] public void CoreHasNoProviderOrFrameworkDependencies()
    {
        var references = typeof(IEngine).Assembly.GetReferencedAssemblies();
        Assert.All(references, reference => Assert.StartsWith("System", reference.Name!, StringComparison.Ordinal));
    }
}
