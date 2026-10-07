using System.Globalization;
using System.Text.Json;
using Dubbing.Core;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Infrastructure.Processes;
using Dubbing.Infrastructure.Configuration;

namespace Dubbing.Media.FFmpeg;

public sealed record FFmpegOptions(string? FFmpegPath = null, string? FFprobePath = null);

public sealed class FFmpegMediaEngine(IProcessRunner runner, IRuntimeLocator locator, FFmpegOptions options, SettingsStore? settings = null) : IMediaEngine
{
    private FFmpegOptions CurrentOptions => settings is null ? options : new(settings.Current.FFmpegPath ?? options.FFmpegPath, settings.Current.FFprobePath ?? options.FFprobePath);
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new(locator.Locate("ffprobe", CurrentOptions.FFprobePath),
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", Path.GetFullPath(path)], TimeSpan.FromSeconds(60)), cancellationToken);
        EnsureSuccess(result);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var root = json.RootElement;
            double seconds = root.TryGetProperty("format", out var format) ? Number(format, "duration") ?? 0 : 0;
            var streams = root.GetProperty("streams").EnumerateArray().Select(s => new MediaStreamInfo(
                s.GetProperty("index").GetInt32(), Text(s, "codec_type") ?? "unknown", Text(s, "codec_name") ?? "unknown",
                (int?)Number(s, "channels"), (int?)Number(s, "sample_rate"), Text(s, "channel_layout"), Text(s, "avg_frame_rate"), Text(s, "time_base"),
                s.TryGetProperty("tags", out var tags) ? Text(tags, "language") : null,
                s.TryGetProperty("tags", out tags) ? Text(tags, "title") : null,
                s.TryGetProperty("disposition", out var disposition) && Number(disposition, "default") == 1)).ToArray();
            if (!double.IsFinite(seconds) || seconds <= 0) throw new MediaProcessingException("Could not determine the file duration.");
            return new(TimeSpan.FromSeconds(seconds), streams);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new MediaProcessingException("ffprobe returned invalid metadata.", ex); }
    }

    public Task ExtractAudioAsync(string input, string output, int sampleRate, int channels, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateFormat(sampleRate, channels);
        return WriteMediaAsync(output, ["-i", Path.GetFullPath(input), "-map", "0:a:0", "-vn", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", channels.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "wav"], progress, null, cancellationToken);
    }

    public Task CutAsync(string input, string output, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (start < TimeSpan.Zero || duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(start));
        return WriteMediaAsync(output, ["-i", Path.GetFullPath(input), "-ss", Seconds(start), "-t", Seconds(duration),
            "-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", "-f", "wav"], null, duration, cancellationToken);
    }

    public Task ExtractTrackAsync(string input, int streamIndex, string output, int sampleRate, int channels,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken, TimeSpan? start = null, TimeSpan? duration = null)
    {
        if (streamIndex < 0) throw new ArgumentOutOfRangeException(nameof(streamIndex));
        ValidateFormat(sampleRate, channels);
        var args = new List<string>();
        if (start is { } offset) { if (offset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(start)); args.AddRange(["-ss", Seconds(offset)]); }
        args.AddRange(["-i", Path.GetFullPath(input), "-map", $"0:{streamIndex}", "-vn", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", channels.ToString(CultureInfo.InvariantCulture), "-af", "aresample=async=1:first_pts=0", "-c:a", "pcm_s16le", "-f", "wav"]);
        if (duration is { } length) { if (length <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration)); args.AddRange(["-t", Seconds(length)]); }
        return WriteMediaAsync(output, args, progress, duration, cancellationToken);
    }

    public Task MixAsync(MixRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateFormat(request.SampleRate, request.Channels);
        if (request.Duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Clips.Count > 24) throw new ArgumentException("Mix at most 24 clips per batch to limit resource usage.");
        var layout = request.Channels switch { 1 => "mono", 2 => "stereo", 3 => "2.1", 4 => "quad", 5 => "5.0", 6 => "5.1", 7 => "6.1", 8 => "7.1", _ => throw new ArgumentOutOfRangeException(nameof(request)) };
        var arguments = new List<string> { "-f", "lavfi", "-i", $"anullsrc=r={request.SampleRate}:cl={layout}" };
        foreach (var clip in request.Clips)
        {
            if (clip.Start < TimeSpan.Zero || !double.IsFinite(clip.Gain) || clip.Gain < 0) throw new ArgumentException("Invalid clip.");
            arguments.AddRange(["-i", Path.GetFullPath(clip.Path)]);
        }
        var filters = new List<string>();
        var labels = "[0:a]";
        for (int i = 0; i < request.Clips.Count; i++)
        {
            var clip = request.Clips[i];
            long delay = checked((long)Math.Round(clip.Start.TotalSeconds * request.SampleRate));
            string trim = clip.Limit is { } limit ? $"atrim=duration={Seconds(limit)}," : "";
            filters.Add($"[{i + 1}:a]{trim}aresample={request.SampleRate},aformat=sample_fmts=fltp:channel_layouts={layout},asetpts=PTS-STARTPTS,volume={Number(clip.Gain)},adelay={delay}S:all=1[a{i}]");
            labels += $"[a{i}]";
        }
        string limiter = request.ProtectPeaks ? ",alimiter=limit=0.95:level=false:latency=true" : "";
        filters.Add($"{labels}amix=inputs={request.Clips.Count + 1}:duration=first:normalize=0{limiter},atrim=duration={Seconds(request.Duration)}[mix]");
        arguments.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[mix]", "-t", Seconds(request.Duration), "-c:a", "pcm_f32le", "-f", "wav"]);
        return WriteMediaAsync(request.OutputPath, arguments, progress, request.Duration, cancellationToken);
    }

    public Task MuxAsync(string video, string audio, string output, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
        => MuxAsync(video, audio, output, new(CopyVideo: Path.GetExtension(output).Equals(".mkv", StringComparison.OrdinalIgnoreCase)), progress, cancellationToken);

    public async Task MuxAsync(string video, string audio, string output, MuxOptions options, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(output).ToLowerInvariant();
        if (extension is not ".mkv" and not ".mp4") throw new ArgumentException("Choose MKV or MP4 export.");
        var info = await ProbeAsync(video, cancellationToken);
        string language = options.Language switch { "es" => "spa", "en" => "eng", "zh" => "zho", "ja" => "jpn", "ar" => "ara", _ => "und" };
        var args = new List<string> { "-i", Path.GetFullPath(video), "-i", Path.GetFullPath(audio) };
        if (options.Subtitles is not null) args.AddRange(["-i", Path.GetFullPath(options.Subtitles)]);
        args.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
        if (options.KeepOriginalAudio) args.AddRange(["-map", "0:a?"]);
        if (options.Subtitles is not null) args.AddRange(["-map", "2:s:0", "-c:s", extension == ".mp4" ? "mov_text" : "srt", "-metadata:s:s:0", "language=" + language]);
        args.AddRange(["-c:v", options.CopyVideo ? "copy" : "libx264", "-c:a", "aac", "-b:a", "192k",
            "-metadata:s:a:0", "title=OpenDub", "-metadata:s:a:0", "language=" + language, "-disposition:a", "0", "-disposition:a:0", "default"]);
        if (!options.CopyVideo) args.AddRange(["-preset", "medium", "-crf", "18", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p"]);
        if (extension == ".mp4") args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-f", extension == ".mp4" ? "mp4" : "matroska"]);
        args.AddRange(["-t", Seconds(info.Duration)]);
        await WriteMediaAsync(output, args, progress, info.Duration, cancellationToken);
    }

    public Task StretchAsync(string input, string output, double rate, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(rate) || rate is < 0.5 or > 2) throw new ArgumentOutOfRangeException(nameof(rate));
        return WriteMediaAsync(output, ["-i", Path.GetFullPath(input), "-af", $"atempo={Number(rate)}", "-c:a", "pcm_s16le", "-f", "wav"], null, null, cancellationToken);
    }
    public Task ExtractFrameAsync(string video, string output, TimeSpan position, CancellationToken cancellationToken) =>
        WriteMediaAsync(output, ["-ss", Seconds(position), "-i", Path.GetFullPath(video), "-frames:v", "1", "-vf", "scale=960:-2", "-c:v", "png", "-f", "image2"], null, null, cancellationToken);

    public async Task<IReadOnlyList<float>> WaveformAsync(string audio, int peakCount, CancellationToken cancellationToken)
    {
        if (peakCount is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(peakCount));
        string temporary = Path.Combine(Path.GetTempPath(), "dubbing-wave-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await ExtractAudioAsync(audio, temporary, 8000, 1, null, cancellationToken);
            using var stream = File.OpenRead(temporary); using var reader = new BinaryReader(stream);
            stream.Position = 12;
            while (stream.Position + 8 <= stream.Length)
            {
                string chunk = new string(reader.ReadChars(4)); uint size = reader.ReadUInt32();
                if (chunk != "data") { stream.Seek(size + size % 2, SeekOrigin.Current); continue; }
                var peaks = new float[peakCount]; long count = size / 2;
                for (long i = 0; i < count; i++)
                {
                    if (i % 8192 == 0) cancellationToken.ThrowIfCancellationRequested();
                    float sample = Math.Abs(reader.ReadInt16() / 32768f);
                    int index = (int)Math.Min(peakCount - 1, i * peakCount / Math.Max(1, count));
                    peaks[index] = Math.Max(peaks[index], sample);
                }
                return peaks;
            }
            return [];
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task WriteMediaAsync(string output, IReadOnlyList<string> arguments, IProgress<EngineProgress>? progress, TimeSpan? duration, CancellationToken cancellationToken)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            progress?.Report(new(0, "Processing audio/video"));
            var all = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-progress", "pipe:1" };
            all.AddRange(arguments); all.Add(temporary);
            var result = await runner.RunAsync(new(locator.Locate("ffmpeg", CurrentOptions.FFmpegPath), all, OnOutputLine: line =>
            {
                if (duration.HasValue && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out long microseconds))
                    progress?.Report(new(Math.Clamp(microseconds / 1_000_000.0 / duration.Value.TotalSeconds, 0, 1), "Processing audio/video"));
            }), cancellationToken);
            EnsureSuccess(result);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new MediaProcessingException("The output file was not generated.");
            File.Move(temporary, output, true);
            progress?.Report(new(1, "Media processing complete."));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0) throw new MediaProcessingException($"The media engine exited with code {result.ExitCode}: {result.StandardError[..Math.Min(1000, result.StandardError.Length)]}");
    }
    private static void ValidateFormat(int sampleRate, int channels)
    {
        if (sampleRate < 8000 || sampleRate > 192000 || channels < 1 || channels > 8) throw new ArgumentOutOfRangeException(nameof(sampleRate));
    }
    private static string Seconds(TimeSpan time) => Number(time.TotalSeconds);
    private static string Number(double number) => number.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.ToString() : null;
    private static double? Number(JsonElement element, string key) => double.TryParse(Text(element, key), CultureInfo.InvariantCulture, out var value) ? value : null;
}
