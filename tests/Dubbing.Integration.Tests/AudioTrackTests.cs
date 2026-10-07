using Dubbing.Application.Media;
using Dubbing.Application.Pipeline;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;
using Dubbing.Engines.Mock;
using Dubbing.Infrastructure.Engines;
using Dubbing.Infrastructure.Processes;
using Dubbing.Infrastructure.Projects;
using Dubbing.Media.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Integration.Tests;

public sealed class AudioTrackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dubbing-tracks-" + Guid.NewGuid());
    private readonly ProcessRunner runner = new(NullLogger<ProcessRunner>.Instance);
    private readonly RuntimeLocator locator = new();
    private FFmpegMediaEngine Media => new(runner, locator, new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG"), Environment.GetEnvironmentVariable("DUBBING_FFPROBE")));

    private async Task<DubbingProject> Fixture()
    {
        Directory.CreateDirectory(root);
        string input = Path.Combine(root, "multi.mkv");
        var result = await runner.RunAsync(new(locator.Locate("ffmpeg", Environment.GetEnvironmentVariable("DUBBING_FFMPEG")),
            ["-v", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=160x90:r=10:d=4", "-f", "lavfi", "-i", "sine=frequency=220:duration=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-f", "lavfi", "-i", "sine=frequency=880:duration=4",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-c:v", "mpeg4", "-c:a", "pcm_s16le",
                "-ac:a:2", "6", "-metadata:s:a:0", "language=fra", "-metadata:s:a:0", "title=Alternate mix",
                "-metadata:s:a:1", "language=eng", "-metadata:s:a:1", "title=Dialogue",
                "-metadata:s:a:2", "title=M&E", "-shortest", input]), default);
        Assert.Equal(0, result.ExitCode);
        var info = await Media.ProbeAsync(input, default);
        var tracks = AudioPreparationService.Discover(info).Select(t => t with { Role = t.Index switch { 2 => AudioTrackRole.Dialogue, 3 => AudioTrackRole.Background, _ => AudioTrackRole.Ignore } }).ToArray();
        string speechFile = Path.Combine(root, "dub.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "Hola", Language = "es", OutputPath = speechFile, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
        return new() { Source = new(input, await ContentFingerprint.ComputeAsync(input, default), info.Duration, 1, 44100), AudioTracks = tracks,
            Settings = new() { AnalysisStreamIndex = 2, BackgroundGain = 1 },
            Segments = [new() { Id = "keep", Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Translation = new("es", "Manual", true), DirtyStages = [],
                GeneratedSpeech = new("dub.wav", TimeSpan.FromSeconds(1), "fixture", await ContentFingerprint.ComputeAsync(speechFile, default)) }] };
    }

    [FFmpegFact] public async Task SeparateTracksPreserveMetadataChannelsEditsAndSharePreviewExportMix()
    {
        var original = await Fixture();
        Assert.Equal("eng", original.AudioTracks.Single(t => t.Index == 2).Language);
        Assert.Equal("Dialogue", original.AudioTracks.Single(t => t.Index == 2).Title);
        Assert.Equal(6, original.AudioTracks.Single(t => t.Index == 3).Channels);
        var service = new AudioPreparationService(Media, new EngineRegistry([]));
        var project = await service.PrepareAsync(original, root, null, default);
        Assert.Same(original.Segments, project.Segments);
        Assert.Same(original.Segments[0].GeneratedSpeech, project.Segments[0].GeneratedSpeech);
        Assert.Contains("track-2.wav", project.SelectedSourceAudio!.Path);
        Assert.Equal(6, (await Media.ProbeAsync(Path.Combine(root, project.Stems["background-3"].Path), default)).Streams.Single().Channels);
        var preview = await ProjectAudioMix.ClipsAsync(project, root, false, null, default);
        var export = await ProjectAudioMix.ClipsAsync(project, root, true, null, default);
        Assert.Equal(preview, export);
        Assert.Equal(2, preview.Count);
        var originalOnly = project with { Settings = project.Settings with { OriginalVoiceMuted = false, DubMuted = true } };
        var originalClips = await ProjectAudioMix.ClipsAsync(originalOnly, root, false, null, default);
        Assert.Contains(originalClips, c => c.Path.Contains("track-2.wav"));
        Assert.DoesNotContain(originalClips, c => c.Path.EndsWith("dub.wav"));
        string originalPreview = await new DubPreviewService(Media).PrepareAsync(originalOnly, root, null, default);
        string dubbedPreview = await new DubPreviewService(Media).PrepareAsync(project, root, null, default);
        Assert.NotEqual(originalPreview, dubbedPreview);
        Assert.Equal(originalPreview, await new DubPreviewService(Media).PrepareAsync(originalOnly, root, null, default));
        Assert.DoesNotContain(preview, c => c.Path.Contains("track-1") || c.Path.Contains("track-2"));
        Assert.Single(await ProjectAudioMix.ClipsAsync(project, root, false, null, default, AudioAudition.OriginalVoice));
        Assert.Single(await ProjectAudioMix.ClipsAsync(project, root, false, null, default, AudioAudition.Background));
        Assert.Equal(3, (await ProjectAudioMix.ClipsAsync(project with { Settings = project.Settings with { OriginalVoiceMuted = false } }, root, false, null, default)).Count);
        Assert.Empty(await ProjectAudioMix.ClipsAsync(project with { Settings = project.Settings with { DubMuted = true, BackgroundMuted = true } }, root, false, null, default));
        string output = await new DubPreviewService(Media).PrepareAsync(project, root, null, default);
        Assert.Equal(6, (await Media.ProbeAsync(output, default)).Streams.Single(s => s.Type == "audio").Channels);
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        await store.SaveAsync(project, root, default);
        var reopened = await store.LoadAsync(root, default);
        Assert.Equal(project.AudioTracks, reopened.AudioTracks);
        Assert.True(reopened.Settings.OriginalVoiceMuted);
        Assert.Equal(project.Stems["background-3"], reopened.Stems["background-3"]);
    }

    [FFmpegFact] public async Task MixedTrackUsesSeparatorAndNeverAddsAlternateMixOrDuplicatesExplicitBackground()
    {
        var original = await Fixture();
        var separator = new Separator();
        var service = new AudioPreparationService(Media, new EngineRegistry([separator]));
        var mixed = original with { AudioTracks = original.AudioTracks.Select(t => t with { Role = t.Index == 2 ? AudioTrackRole.Mixed : AudioTrackRole.Ignore }).ToArray(),
            Engines = new() { Separation = separator.Descriptor.EngineId }, Settings = original.Settings with { UseSeparation = true } };
        var prepared = await service.PrepareAsync(mixed, root, null, default);
        Assert.Equal(1, separator.Calls);
        Assert.True(prepared.Stems.ContainsKey("vocals")); Assert.True(prepared.Stems.ContainsKey("instrumental"));
        Assert.Same(mixed.Segments, prepared.Segments);
        await service.PrepareAsync(prepared, root, null, default); Assert.Equal(1, separator.Calls);
        var directBackground = mixed with { AudioTracks = mixed.AudioTracks.Select(t => t.Index == 3 ? t with { Role = AudioTrackRole.Background } : t).ToArray() };
        var combined = await service.PrepareAsync(directBackground, root, null, default);
        Assert.True(combined.Stems.ContainsKey("background-3")); Assert.False(combined.Stems.ContainsKey("instrumental"));
        var without = await service.PrepareAsync(mixed with { Settings = mixed.Settings with { UseSeparation = false } }, root, null, default);
        Assert.Empty(without.Stems);
        var fullMix = combined with { Stems = combined.Stems.Where(s => s.Key != "vocals").ToDictionary(), Settings = combined.Settings with { OriginalVoiceMuted = false, DubMuted = true } };
        var fullMixClips = await ProjectAudioMix.ClipsAsync(fullMix, root, false, null, default);
        Assert.Single(fullMixClips);
        Assert.Equal(Path.Combine(root, fullMix.SelectedSourceAudio!.Path), fullMixClips[0].Path);
        Assert.Single(await ProjectAudioMix.ClipsAsync(without, root, false, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectAudioMix.ClipsAsync(without, root, false, null, default, AudioAudition.Background));
    }

    private sealed class Separator : ISeparationEngine
    {
        public int Calls;
        public EngineDescriptor Descriptor => new MockSeparationEngine().Descriptor;
        public Task<SeparationResult> SeparateAsync(SeparationRequest request, IProgress<EngineProgress>? progress, CancellationToken ct)
        {
            Calls++; Directory.CreateDirectory(request.OutputDirectory);
            var stems = new Dictionary<string, AudioReference>();
            foreach (string key in new[] { "vocals", "instrumental" })
            { string path = Path.Combine(request.OutputDirectory, key + ".wav"); File.Copy(request.Audio.Path, path, true); stems[key] = new(path); }
            return Task.FromResult(new SeparationResult(stems));
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
