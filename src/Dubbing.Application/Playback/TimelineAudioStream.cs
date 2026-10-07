using System.Buffers.Binary;
using Dubbing.Core.Media;

namespace Dubbing.Application.Playback;

// Seekable virtual WAV. VLC asks for small PCM blocks; no whole-project WAV is written.
public sealed class TimelineAudioStream : Stream
{
    public const int Rate = 48000;
    private const int HeaderSize = 80; // RF64 supports movies longer than the RIFF 4 GiB limit.
    private readonly object gate = new();
    private readonly byte[] header = new byte[HeaderSize];
    private readonly Dictionary<string, (PcmWaveSource Source, long Length, long Stamp)> sources = [];
    private IReadOnlyList<AudioClip> clips = [];
    private long position;
    private bool disposed;
    private readonly Dictionary<int, float[][]> kernels = [];
    private readonly float[] mixBuffer = new float[8194];
    private readonly byte[] pcmBuffer = new byte[16388];
    public int OpenSources { get { lock (gate) return sources.Count; } }
    public long SourceBytesRead { get { lock (gate) return sources.Values.Sum(s => s.Source.BytesRead); } }
    public override long Length { get; }
    public TimelineAudioStream(TimeSpan duration)
    {
        long frames = checked((long)Math.Round(duration.TotalSeconds * Rate));
        Length = checked(HeaderSize + frames * 4);
        "RF64"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), uint.MaxValue); "WAVEds64"u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 28);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20), Length - 8);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28), Length - HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(36), frames);
        "fmt "u8.CopyTo(header.AsSpan(48)); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(56), 1); BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(58), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), Rate); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(64), Rate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(68), 4); BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(70), 16);
        "data"u8.CopyTo(header.AsSpan(72)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), uint.MaxValue);
    }
    public void Update(IReadOnlyList<AudioClip> next)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Lazy-open around the cursor, retaining unchanged source caches across updates.
            var keep = next.Select(c => c.Path).ToHashSet();
            foreach (string path in sources.Keys.ToArray())
            {
                var info = new FileInfo(path); var current = sources[path];
                if (!keep.Contains(path) || !info.Exists || info.Length != current.Length || info.LastWriteTimeUtc.Ticks != current.Stamp)
                { current.Source.Dispose(); sources.Remove(path); }
            }
            clips = next.ToArray();
        }
    }
    private PcmWaveSource Source(string path)
    {
        if (sources.TryGetValue(path, out var cached)) return cached.Source;
        // Bound open files and cached blocks for long projects.
        if (sources.Count >= 32) { string first = sources.Keys.First(); sources[first].Source.Dispose(); sources.Remove(first); }
        var info = new FileInfo(path); var source = new PcmWaveSource(path);
        sources.Add(path, (source, info.Length, info.LastWriteTimeUtc.Ticks)); return source;
    }
    private float Resample(PcmWaveSource source, double frame, int channel)
    {
        long at = (long)Math.Floor(frame);
        if (source.SampleRate == Rate) return source.Sample(at, channel);
        if (!kernels.TryGetValue(source.SampleRate, out var phases))
        {
            phases = new float[256][]; double cutoff = .94 * Math.Min(1d, (double)Rate / source.SampleRate);
            for (int p = 0; p < phases.Length; p++)
            {
                var weights = new float[16]; double sum = 0;
                for (int t = 0; t < 16; t++)
                {
                    double x = t - 7 - p / 256d;
                    double sinc = Math.Abs(x) < 1e-10 ? cutoff : Math.Sin(Math.PI * cutoff * x) / (Math.PI * x);
                    weights[t] = (float)(sinc * (.5 + .5 * Math.Cos(Math.PI * x / 8))); sum += weights[t];
                }
                for (int t = 0; t < 16; t++) weights[t] /= (float)sum;
                phases[p] = weights;
            }
            kernels.Add(source.SampleRate, phases);
        }
        var kernel = phases[Math.Min(255, (int)((frame - at) * 256))]; float value = 0;
        for (int t = 0; t < 16; t++) value += source.Sample(at + t - 7, channel) * kernel[t];
        return value;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            count = (int)Math.Min(Math.Min(count, 16384), Length - position);
            int written = 0;
            if (position < HeaderSize)
            {
                int n = Math.Min(count, HeaderSize - (int)position); Array.Copy(header, position, buffer, offset, n);
                position += n; written += n;
            }
            int remaining = count - written;
            if (remaining == 0) return written;
            long byteOffset = position - HeaderSize, firstFrame = byteOffset / 4;
            int skip = (int)(byteOffset % 4), frames = (remaining + skip + 3) / 4;
            var mix = mixBuffer; Array.Clear(mix, 0, frames * 2);
            double start = (double)firstFrame / Rate, end = (double)(firstFrame + frames) / Rate;
            foreach (var clip in clips)
            {
                if (clip.Gain == 0 || clip.Start.TotalSeconds >= end || clip.Limit is { } limit && clip.Start.TotalSeconds + limit.TotalSeconds <= start) continue;
                var source = Source(clip.Path);
                double seconds = Math.Min((double)source.Frames / source.SampleRate, clip.Limit?.TotalSeconds ?? double.MaxValue);
                long begin = (long)Math.Round(clip.Start.TotalSeconds * Rate);
                long finish = begin + (long)Math.Round(seconds * Rate);
                int a = (int)Math.Clamp(begin - firstFrame, 0, frames), b = (int)Math.Clamp(finish - firstFrame, 0, frames);
                for (int i = a; i < b; i++)
                {
                    double input = (firstFrame + i - begin) * ((double)source.SampleRate / Rate);
                    mix[2 * i] += Resample(source, input, 0) * (float)clip.Gain;
                    mix[2 * i + 1] += Resample(source, input, 1) * (float)clip.Gain;
                }
            }
            var pcm = pcmBuffer;
            for (int i = 0; i < frames * 2; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(mix[i], -.95f, .95f) * 32767));
            Array.Copy(pcm, skip, buffer, offset + written, remaining); position += remaining;
            return count;
        }
    }
    public override long Seek(long offset, SeekOrigin origin)
    { lock (gate) { long target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => Length + offset }; if (target < 0 || target > Length) throw new IOException("Seek outside timeline."); return position = target; } }
    public override long Position { get { lock (gate) return position; } set => Seek(value, SeekOrigin.Begin); }
    public override bool CanRead => !disposed; public override bool CanSeek => !disposed; public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { lock (gate) { disposed = true; foreach (var s in sources.Values) s.Source.Dispose(); sources.Clear(); } base.Dispose(disposing); }
}
