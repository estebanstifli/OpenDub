using Dubbing.Core.Engines;

namespace Dubbing.Core.Media;

public sealed record MediaStreamInfo(int Index, string Type, string Codec, int? Channels = null,
    int? SampleRate = null, string? ChannelLayout = null, string? FrameRate = null, string? TimeBase = null,
    string? Language = null, string? Title = null, bool IsDefault = false);
public sealed record MediaInfo(TimeSpan Duration, IReadOnlyList<MediaStreamInfo> Streams);
public sealed record AudioClip(string Path, TimeSpan Start, double Gain = 1, TimeSpan? Limit = null);
public sealed record MixRequest(IReadOnlyList<AudioClip> Clips, string OutputPath, TimeSpan Duration,
    int SampleRate = 48000, int Channels = 2, bool ProtectPeaks = true);

public sealed record MuxOptions(bool CopyVideo = false, bool KeepOriginalAudio = false, string? Subtitles = null, string Language = "es");

public interface IMediaEngine
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken);
    Task ExtractAudioAsync(string input, string output, int sampleRate, int channels,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
    Task ExtractTrackAsync(string input, int streamIndex, string output, int sampleRate, int channels,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken, TimeSpan? start = null, TimeSpan? duration = null);
    Task CutAsync(string input, string output, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken);
    Task MixAsync(MixRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
    Task MuxAsync(string video, string audio, string output, IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
    Task MuxAsync(string video, string audio, string output, MuxOptions options, IProgress<EngineProgress>? progress, CancellationToken ct)
        => MuxAsync(video, audio, output, progress, ct);
    Task StretchAsync(string input, string output, double rate, CancellationToken cancellationToken);
    Task ExtractFrameAsync(string video, string output, TimeSpan position, CancellationToken cancellationToken);
    Task<IReadOnlyList<float>> WaveformAsync(string audio, int peakCount, CancellationToken cancellationToken);
}
