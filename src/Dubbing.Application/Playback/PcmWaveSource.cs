using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Dubbing.Application.Playback;

// Bounded source cache: four 4096-frame blocks, independent of movie length.
public sealed class PcmWaveSource : IDisposable
{
    private readonly SafeFileHandle file;
    private readonly long data;
    private readonly int channels, bytesPerSample, blockAlign, format;
    private readonly Dictionary<long, float[]> blocks = [];
    private readonly Queue<long> order = [];
    private const int FramesPerBlock = 4096;
    public int SampleRate { get; }
    public long Frames { get; }
    public long BytesRead { get; private set; }
    public PcmWaveSource(string path)
    {
        file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            string riff = new(reader.ReadChars(4)); reader.ReadUInt32();
            if (riff is not ("RIFF" or "RF64") || new string(reader.ReadChars(4)) != "WAVE") throw new NotSupportedException("Not PCM WAV: " + path);
            long rf64Length = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                string name = new(reader.ReadChars(4)); uint length = reader.ReadUInt32(); long start = stream.Position;
                if (name == "ds64" && length >= 24) { reader.ReadInt64(); rf64Length = reader.ReadInt64(); }
                if (name == "fmt " && length >= 16)
                {
                    format = reader.ReadUInt16(); channels = reader.ReadUInt16(); SampleRate = reader.ReadInt32(); reader.ReadInt32();
                    blockAlign = reader.ReadUInt16(); bytesPerSample = reader.ReadUInt16() / 8;
                    if (format == 65534 && length >= 40) { stream.Position = start + 24; format = reader.ReadUInt16(); }
                }
                if (name == "data")
                {
                    data = start;
                    long bytes = length == uint.MaxValue && rf64Length > 0 ? rf64Length : length;
                    if (channels is < 1 or > 2 || SampleRate is < 8000 or > 192000 ||
                        format is not (1 or 3) || bytesPerSample is < 2 or > 4 || format == 3 && bytesPerSample != 4 || blockAlign != channels * bytesPerSample)
                        throw new NotSupportedException("WAV requires preview conversion: " + path);
                    Frames = Math.Min(bytes, stream.Length - data) / blockAlign;
                    return;
                }
                stream.Position = checked(start + length + length % 2);
            }
            throw new InvalidDataException("WAV has no audio data: " + path);
        }
        catch { file.Dispose(); throw; }
    }

    public float Sample(long frame, int channel)
    {
        if (frame < 0 || frame >= Frames) return 0;
        long block = frame / FramesPerBlock;
        if (!blocks.TryGetValue(block, out var values))
        {
            int count = (int)Math.Min(FramesPerBlock, Frames - block * FramesPerBlock);
            var bytes = new byte[count * blockAlign]; int got = 0;
            while (got < bytes.Length)
            {
                int n = RandomAccess.Read(file, bytes.AsSpan(got), data + block * FramesPerBlock * blockAlign + got);
                if (n == 0) throw new EndOfStreamException("WAV changed during playback.");
                got += n;
            }
            BytesRead += got; values = new float[count * channels];
            for (int i = 0; i < values.Length; i++)
            {
                var b = bytes.AsSpan(i * bytesPerSample, bytesPerSample);
                float value = format == 3 ? BinaryPrimitives.ReadSingleLittleEndian(b) : bytesPerSample switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
                    3 => ((b[0] | b[1] << 8 | b[2] << 16) << 8 >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f
                };
                values[i] = float.IsFinite(value) ? value : 0;
            }
            while (blocks.Count >= 4) blocks.Remove(order.Dequeue());
            blocks.Add(block, values); order.Enqueue(block);
        }
        return values[(int)(frame % FramesPerBlock) * channels + Math.Min(channel, channels - 1)];
    }
    public void Dispose() { file.Dispose(); blocks.Clear(); order.Clear(); }
}
