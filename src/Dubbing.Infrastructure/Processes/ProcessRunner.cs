using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Dubbing.Infrastructure.Processes;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null, Action<string>? OnOutputLine = null, string? WorkingDirectory = null);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    internal static void ConfigureNativeLibraries(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(start.FileName)) return;
        // Each inference engine ships its own ggml ABI. Prefer its adjacent libraries
        // over AppImage's shared VLC dependencies inherited from the parent process.
        string directory = Path.GetDirectoryName(start.FileName)!;
        start.Environment.TryGetValue("LD_LIBRARY_PATH", out string? inherited);
        start.Environment["LD_LIBRARY_PATH"] = directory + (string.IsNullOrEmpty(inherited) ? "" : ":" + inherited);
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true
        };
        if (request.WorkingDirectory is { } directory) start.WorkingDirectory = directory;
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } limit) timeout.CancelAfter(limit);
        var watch = Stopwatch.StartNew();
        bool versionProbe = request.Arguments.Count == 1 && request.Arguments[0] == "--version";
        if (versionProbe) logger.LogDebug("Checking runtime version: {Executable}", Path.GetFileName(request.Executable));
        else logger.LogDebug("Starting {Executable}", Path.GetFileName(request.Executable));
        ConfigureNativeLibraries(start);
        process.Start();
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput, request.OnOutputLine);
        var error = DrainAsync(process.StandardError, null);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Exited between HasExited and Kill. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Process {Path.GetFileName(request.Executable)} timed out.");
        }
        var result = new ProcessResult(process.ExitCode, await output, await error);
        // Arguments can contain source paths or text; deliberately omit them from logs.
        if (!versionProbe || result.ExitCode != 0)
            logger.Log(result.ExitCode == 0 ? LogLevel.Information : LogLevel.Error,
                "{Executable} completed · exit {ExitCode} · {Seconds:F2} s", Path.GetFileName(request.Executable), result.ExitCode, watch.Elapsed.TotalSeconds);
        return result;
    }

    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? onLine)
    {
        const int maximumCharacters = 1_048_576;
        var result = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (result.Length < maximumCharacters)
            {
                int count = Math.Min(line.Length, maximumCharacters - result.Length);
                result.Append(line.AsSpan(0, count)).AppendLine();
            }
            onLine?.Invoke(line);
        }
        return result.ToString();
    }
}
