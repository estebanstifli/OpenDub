using System.Runtime.InteropServices;
using Dubbing.Core.Engines;

namespace Dubbing.Infrastructure.Configuration;

public sealed class ExecutionMetadata(SettingsStore settings) : IExecutionMetadata
{
    public IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["platform"] = RuntimeInformation.RuntimeIdentifier,
        ["configuredBackend"] = settings.Current.Backend,
        ["threads"] = settings.Current.Threads.ToString(),
        ["device"] = settings.Current.Device.ToString(),
        ["translationModelFile"] = Path.GetFileName(settings.Current.TranslationModelPath) ?? ""
    };
}
