using Dubbing.Engines.LlamaCpp;

namespace Dubbing.Integration.Tests;

public sealed class LlamaDeviceTests
{
    [Theory]
    [InlineData("Available devices:\n  MTL0: Apple Paravirtual device (4778 MiB)", "metal", 0, "MTL0")]
    [InlineData("  Metal: Apple M2", "metal", 0, "Metal")]
    [InlineData("  Metal0: Apple M2", "metal", 0, "Metal0")]
    [InlineData("  CUDA0: NVIDIA GeForce RTX 3070", "cuda", 0, "CUDA0")]
    [InlineData("  CUDA0: NVIDIA GeForce RTX 3070", "cuda", 1, null)]
    [InlineData("  MTL0: Apple M2", "metal", 1, null)]
    [InlineData("  BLAS: Accelerate", "metal", 0, null)]
    public void UsesTheExactEnumeratedDeviceName(string output, string backend, int index, string? expected)
        => Assert.Equal(expected, LlamaCppTranslationEngine.FindGpuDevice(output, backend, index));
}
