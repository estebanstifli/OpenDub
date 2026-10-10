using Dubbing.Engines.AudioCpp;
using Dubbing.Infrastructure.Processes;

namespace Dubbing.Integration.Tests;

public sealed class AudioRuntimeSelectorTests
{
    [Theory]
    [InlineData("CUDA:0 \"RTX 3070\" [GPU]", "cuda", 0, true)]
    [InlineData("Metal:0 \"Apple M2\" [IGPU]", "metal", 0, true)]
    [InlineData("MTL:0 \"Apple Paravirtual device\" [GPU]", "metal", 0, true)]
    [InlineData("ROCm:0 \"AMD Radeon\" [GPU]", "hip", 0, true)]
    [InlineData("Vulkan:0 \"llvmpipe\" [CPU]", "vulkan", 0, false)]
    [InlineData("select with: --backend <cuda|metal|cpu>", "cuda", 0, false)]
    [InlineData("CUDA:1 \"RTX\" [GPU]", "cuda", 0, false)]
    public void RequiresARealDeviceAtTheRequestedIndex(string output, string backend, int index, bool expected)
        => Assert.Equal(expected, AudioRuntimeSelector.HasDevice(output, backend, index));

    [Fact]
    public async Task AutoFallsBackToCpuAndExplicitCudaFailsWithoutDevice()
    {
        var runner = new ProbeRunner(); var selector = new AudioRuntimeSelector(new Locator(), runner);
        Assert.Equal("cpu", (await selector.ResolveAsync("audiocpp_server", null, "auto", 0, default)).Backend);
        await Assert.ThrowsAsync<Dubbing.Core.EngineException>(() => selector.ResolveAsync("audiocpp_server", null, "cuda", 0, default));
    }
    [Fact]
    public async Task ConfiguredGpuRuntimeIsProbedOnceAndReused()
    {
        var runner = new ProbeRunner { Output = "CUDA:0 \"RTX\" [GPU]" };
        var selector = new AudioRuntimeSelector(new Locator(), runner);
        var first = await selector.ResolveAsync("audiocpp_server", "/custom/server", "cuda", 0, default);
        Assert.Equal(("/custom/server", "cuda"), first);
        Assert.Equal(first, await selector.ResolveAsync("audiocpp_server", "/custom/server", "cuda", 0, default));
        Assert.Equal(1, runner.Calls);
    }
    private sealed class Locator : IRuntimeLocator
    {
        public string Locate(string executable, string? configuredPath = null) => configuredPath ?? executable;
    }
    private sealed class ProbeRunner : IProcessRunner
    {
        public string Output = "available_devices=0";
        public int Calls;
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        { Calls++; Assert.Equal("--list-devices", request.Arguments.Single()); return Task.FromResult(new ProcessResult(0, Output, "")); }
    }
}
