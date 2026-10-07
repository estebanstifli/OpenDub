using System.Buffers.Binary;
using Dubbing.Core.Engines;

namespace Dubbing.Engines.Mock;

// A deterministic tone makes rendering testable without speech models or network requests.
public sealed class MockTtsEngine(string engineId = "mock/tone", string name = "Speech tone · demo", double frequency = 440) : ITtsEngine
{
    public EngineDescriptor Descriptor { get; } = MockDescriptor.Create(engineId, name, EngineTask.Tts,
        new() { SupportsTargetDuration = true });

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        const int sampleRate = 16000;
        var duration = request.TargetDuration ?? TimeSpan.FromSeconds(2);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        int samples = checked((int)Math.Round(duration.TotalSeconds * sampleRate));
        var output = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true))
            {
                using (var header = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
                {
                    header.Write("RIFF"u8); header.Write(36 + samples * 2); header.Write("WAVEfmt "u8);
                    header.Write(16); header.Write((short)1); header.Write((short)1);
                    header.Write(sampleRate); header.Write(sampleRate * 2); header.Write((short)2); header.Write((short)16);
                    header.Write("data"u8); header.Write(samples * 2);
                }
                byte[] buffer = new byte[8192];
                for (int offset = 0; offset < samples;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(buffer.Length / 2, samples - offset);
                    for (int i = 0; i < count; i++)
                    {
                        int position = offset + i;
                        double fade = Math.Min(1, Math.Min(position, samples - 1 - position) / 160.0);
                        short value = (short)(Math.Sin(2 * Math.PI * frequency * position / sampleRate) * 3000 * fade);
                        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), value);
                    }
                    await stream.WriteAsync(buffer.AsMemory(0, count * 2), cancellationToken);
                    offset += count;
                    progress?.Report(new((double)offset / samples, "Generating demonstration tone"));
                }
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
            return new(new(output), TimeSpan.FromSeconds((double)samples / sampleRate), sampleRate, 1);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
