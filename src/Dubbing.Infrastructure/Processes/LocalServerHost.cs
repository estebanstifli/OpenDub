using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dubbing.Core;

namespace Dubbing.Infrastructure.Processes;

public sealed class LocalServerHost : IDisposable
{
    private Process? process;
    private string? configuration;
    private Uri? endpoint;
    private string lastError = "";
    public Uri? Endpoint => process is { HasExited: false } ? endpoint : null;
    public async Task<Uri> EnsureAsync(string executable, string configurationKey, Func<int, IReadOnlyList<string>> arguments, CancellationToken ct)
    {
        configurationKey = executable + "\n" + configurationKey;
        if (process is { HasExited: false } && configuration == configurationKey) return endpoint!;
        Stop();
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        endpoint = new Uri($"http://127.0.0.1:{port}/");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in arguments(port)) start.ArgumentList.Add(arg);
        process = new Process { StartInfo = start };
        lastError = "";
        process.OutputDataReceived += (_, e) => LogRuntimeLine(e.Data);
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) { lastError = e.Data[..Math.Min(e.Data.Length, 2000)]; LogRuntimeLine(e.Data); } };
        ActivityJournal.Write($"Starting local runtime {Path.GetFileName(executable)} on port {port}.");
        process.Start(); process.StandardInput.Close(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new RuntimeCrashedException($"Local runtime exited ({process.ExitCode}). {lastError}");
                try
                {
                    using var response = await client.GetAsync(new Uri(endpoint, "health"), ct);
                    if (response.IsSuccessStatusCode) { configuration = configurationKey; ActivityJournal.Write("Local runtime is ready."); return endpoint; }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(150, ct);
            }
            throw new TimeoutException("Local runtime did not become ready within five minutes.");
        }
        catch { Stop(); throw; }
    }
    private static void LogRuntimeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (new[] { "error", "warning", "assert", "cuda", "backend", "device", "vram", "loading model", "loaded model", "unload", "offload", "buffer size" }
            .Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)))
            ActivityJournal.Write("Runtime · " + line[..Math.Min(line.Length, 2000)]);
    }
    public void Stop()
    {
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); process = null; configuration = null; }
    }
    public string FailureDescription()
    {
        if (process is { HasExited: true }) return $"Local audio runtime exited ({process.ExitCode}). {lastError}";
        return $"The connection to the local audio runtime was interrupted. {lastError}";
    }
    public void Dispose() => Stop();
}
