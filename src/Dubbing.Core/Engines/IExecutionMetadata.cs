namespace Dubbing.Core.Engines;

public interface IExecutionMetadata
{
    IReadOnlyDictionary<string, string> Describe();
}
