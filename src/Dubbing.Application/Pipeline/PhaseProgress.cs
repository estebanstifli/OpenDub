using Dubbing.Core.Engines;

namespace Dubbing.Application.Pipeline;

// Synchronous forwarding keeps ordered phase boundaries even on a UI synchronization context.
internal sealed class PhaseProgress(IProgress<EngineProgress>? target, double start, double length, string phase) : IProgress<EngineProgress>
{
    public void Report(EngineProgress value) => target?.Report(value with
    {
        Fraction = start + Math.Clamp(value.Fraction, 0, 1) * length,
        Message = phase + " · " + value.Message
    });
}
