using System.Buffers.Binary;
using Dubbing.Application.Engines;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core;
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

public sealed class FFmpegFactAttribute : FactAttribute
{
    public FFmpegFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FFMPEG_TESTS") != "1")
            Skip = "Set FFMPEG_TESTS=1 to run tests with a locally installed FFmpeg/ffprobe. No downloads.";
    }
}

public sealed class MediaPipelineTests : IDisposable
{
    private sealed class ProgressLog : IProgress<EngineProgress>
    {
        public List<EngineProgress> Values { get; } = [];
        public void Report(EngineProgress value) { lock (Values) Values.Add(value); }
    }

    [FFmpegFact]
    public async Task Mp4ExportReportsProgressAndRequiresExplicitTimingOverride()
    {
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "odd.mkv"), voice = Path.Combine(directory, "voice.wav");
        var fixture = await runner.RunAsync(new(FFmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i",
            "testsrc=s=161x91:r=10:d=3", "-c:v", "ffv1", video]), default);
        Assert.Equal(0, fixture.ExitCode);
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "test", Language = "es", OutputPath = voice, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
        var audio = new GeneratedSpeech("voice.wav", TimeSpan.FromSeconds(1), "test", await Dubbing.Application.Media.ContentFingerprint.ComputeAsync(voice, default));
        var segment = new DubSegment { Start = TimeSpan.FromSeconds(.5), End = TimeSpan.FromSeconds(2),
            Translation = new("es", "Una prueba."), GeneratedSpeech = audio, Status = SegmentStatus.Error, DirtyStages = [] };
        var project = new DubbingProject { Source = new(video, await Dubbing.Application.Media.ContentFingerprint.ComputeAsync(video, default), TimeSpan.FromSeconds(3), 1, 16000), Segments = [segment] };
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        var export = new ExportVideoService(Media, new DubRenderService(Media), store);
        var options = new ExportOptions { Directory = Path.Combine(directory, "export") };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => export.ExportAsync(project, directory, null, default, options));
        Assert.Contains("Segment 1", failure.Message);
        var log = new ProgressLog();
        var result = await export.ExportAsync(project, directory, log, default, options with { AllowDurationMismatch = true });
        Assert.Equal(SegmentStatus.Error, result.Segments[0].Status); // Exporting does not certify bad timing as fixed.
        var info = await Media.ProbeAsync(Path.Combine(options.Directory, "dubbed.mp4"), default);
        Assert.Contains(info.Streams, s => s.Codec == "h264");
        Assert.Contains(info.Streams, s => s.Codec == "mov_text");
        Assert.InRange(info.Duration.TotalSeconds, 2.99, 3.1);
        Assert.Contains(log.Values, p => p.Message.StartsWith("Export video") && p.Fraction > .30 && p.Fraction < .85);
        Assert.Equal(1, log.Values.Last().Fraction);
        Assert.True(log.Values.Zip(log.Values.Skip(1)).All(p => p.First.Fraction <= p.Second.Fraction));
        // The timing override must never bypass content-integrity validation.
        await File.AppendAllTextAsync(voice, "external edit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => export.ExportAsync(project, directory, null, default,
            options with { Directory = Path.Combine(directory, "changed"), AllowDurationMismatch = true }));
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dubbing test ' español " + Guid.NewGuid());
    private readonly ProcessRunner runner = new(NullLogger<ProcessRunner>.Instance);
    private readonly RuntimeLocator locator = new();
    private FFmpegMediaEngine Media => new(runner, locator,
        new(Environment.GetEnvironmentVariable("DUBBING_FFMPEG"), Environment.GetEnvironmentVariable("DUBBING_FFPROBE")));
    private string FFmpeg => locator.Locate("ffmpeg", Environment.GetEnvironmentVariable("DUBBING_FFMPEG"));

    [FFmpegFact]
    public async Task DubPreviewReplacesOriginalAudioAndPlacesAllSpeechOnVideoTimeline()
    {
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "source.mp4"), voice = Path.Combine(directory, "voice.wav");
        var fixture = await runner.RunAsync(new(FFmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=160x90:r=10:d=6",
            "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=16000:duration=6", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", video]), default);
        Assert.Equal(0, fixture.ExitCode);
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "test", Language = "es", OutputPath = voice, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
        var speech = new GeneratedSpeech("voice.wav", TimeSpan.FromSeconds(1), "test");
        var project = new DubbingProject { Source = new(video, "fixture", TimeSpan.FromSeconds(6), 1, 16000),
            Segments = [new() { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), GeneratedSpeech = speech },
                new() { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4), GeneratedSpeech = speech },
                new() { Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(5) },
                new() { Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(6), GeneratedSpeech = speech, ExcludedFromDubbing = true }] };
        var preview = new DubPreviewService(Media);
        string output = await preview.PrepareAsync(project, directory, null, default);
        var info = await Media.ProbeAsync(output, default);
        Assert.Single(info.Streams, s => s.Type == "audio");
        Assert.Contains(info.Streams, s => s.Type == "video" && s.Codec == "mpeg4");
        Assert.InRange(info.Duration.TotalSeconds, 5.99, 6.1);
        foreach (double time in new[] { .3, 1.3, 2.3, 3.3, 4.3, 5.3 })
        {
            string cut = Path.Combine(directory, "sample.wav");
            await Media.CutAsync(output, cut, TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(.3), default);
            float peak = (await Media.WaveformAsync(cut, 30, default)).Max();
            if (time is 1.3 or 3.3) Assert.True(peak > .05, $"No dialogue at {time}");
            else Assert.True(peak < .001, $"Unexpected original/excluded audio at {time}: {peak}");
        }
        Assert.Equal(output, await preview.PrepareAsync(project, directory, null, default));
        await new MockTtsEngine(frequency: 880).SynthesizeAsync(new() { Text = "changed", Language = "es", OutputPath = voice, TargetDuration = TimeSpan.FromSeconds(1) }, null, default);
        Assert.NotEqual(output, await preview.PrepareAsync(project, directory, null, default));
    }

    [FFmpegFact]
    [Trait("Requires", "FFMPEG")]
    public async Task VideoToMockSpeechToPlayableVideoSurvivesReopenAndUsesIncrementalCache()
    {
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "source test.mp4");
        var fixture = await runner.RunAsync(new(FFmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=160x90:r=10:d=4",
            "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=16000:duration=4", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", video]), CancellationToken.None);
        Assert.Equal(0, fixture.ExitCode);
        string projectDirectory = Path.Combine(directory, "film.dubproj");
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        var registry = new EngineRegistry(MockEngineCatalog.Create());
        var import = new ImportVideoService(Media, store, registry, new TimelineBuilder());
        var speech = new GenerateSpeechService(new EngineSelectionService(registry), store);
        var project = await import.ImportAsync(new ProjectFactory(registry).Create(), video, projectDirectory, null, CancellationToken.None);
        Assert.Equal(2, project.Speakers.Count);
        Assert.Equal(2, project.Segments.Count);
        project = await new TranslationService(registry).TranslateAsync(project, null, CancellationToken.None);
        project = await speech.GenerateAsync(project, projectDirectory, null, CancellationToken.None);
        var first = project.Segments[0].GeneratedSpeech!;
        var second = project.Segments[1].GeneratedSpeech!;
        string firstFile = Path.Combine(projectDirectory, first.File);
        var savedTime = File.GetLastWriteTimeUtc(firstFile);
        project = await store.LoadAsync(projectDirectory, CancellationToken.None);
        project = await speech.GenerateAsync(project, projectDirectory, null, CancellationToken.None);
        Assert.Equal(savedTime, File.GetLastWriteTimeUtc(firstFile));
        project = ProjectEditor.EditTranslation(project, project.Segments[0].Id, "Una edición manual.");
        project = await speech.GenerateAsync(project, projectDirectory, null, CancellationToken.None);
        Assert.NotEqual(first.CacheKey, project.Segments[0].GeneratedSpeech!.CacheKey);
        Assert.Equal(second, project.Segments[1].GeneratedSpeech);
        var beforeRegenerate = project.Segments[0].GeneratedSpeech!;
        project = await speech.GenerateAsync(project, projectDirectory, null, default, project.Segments[0].Id, overwriteExisting: true);
        Assert.NotEqual(beforeRegenerate.File, project.Segments[0].GeneratedSpeech!.File);
        Assert.Equal(beforeRegenerate.Duration, project.Segments[0].GeneratedSpeech!.Duration);
        Assert.Equal(second, project.Segments[1].GeneratedSpeech);
        Assert.Equal(beforeRegenerate.Fingerprint, await Dubbing.Application.Media.ContentFingerprint.ComputeAsync(Path.Combine(projectDirectory, beforeRegenerate.File), default));
        // Corrupted cached audio is regenerated rather than silently reused.
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, second.File), "corrupt");
        project = await speech.GenerateAsync(project, projectDirectory, null, CancellationToken.None);
        Assert.Equal(second.Fingerprint, project.Segments[1].GeneratedSpeech!.Fingerprint);
        var export = new ExportVideoService(Media, new DubRenderService(Media), store);
        project = await export.ExportAsync(project, projectDirectory, null, CancellationToken.None);
        var output = Path.Combine(projectDirectory, "renders", "dubbed.mkv");
        var probe = await Media.ProbeAsync(output, CancellationToken.None);
        Assert.Contains(probe.Streams, x => x.Type == "video");
        Assert.Contains(probe.Streams, x => x.Type == "audio");
        Assert.InRange(probe.Duration.TotalSeconds, 3.9, 4.2);
        var decoded = await runner.RunAsync(new(FFmpeg, ["-v", "error", "-i", output, "-f", "null", "-"]), CancellationToken.None);
        Assert.Equal(0, decoded.ExitCode);
        Assert.All(project.Segments, s => Assert.Equal(SegmentStatus.Ready, s.Status));
        Assert.True((await store.LoadAsync(projectDirectory, CancellationToken.None)).Segments[0].Translation.Locked);
        Assert.Contains("-->", await File.ReadAllTextAsync(Path.Combine(projectDirectory, "renders", "subtitles.srt")));
        project = project with { Settings = project.Settings with { ExportContainer = "mp4" } };
        await export.ExportAsync(project, projectDirectory, null, default);
        string mp4 = Path.Combine(projectDirectory, "renders", "dubbed.mp4");
        Assert.Contains((await Media.ProbeAsync(mp4, default)).Streams, s => s.Codec == "h264");
        string multi = Path.Combine(projectDirectory, "exports", "multi");
        await export.ExportAsync(project, projectDirectory, null, default, new() { Directory = multi, Profile = "Preserve video quality (MKV)", KeepOriginalAudio = true, EmbedSubtitles = true });
        var multiInfo = await Media.ProbeAsync(Path.Combine(multi, "dubbed.mkv"), default);
        Assert.Equal(2, multiInfo.Streams.Count(s => s.Type == "audio"));
        Assert.Contains(multiInfo.Streams, s => s.Type == "subtitle");
        Assert.Contains(multiInfo.Streams, s => s.Type == "video" && s.Codec == "mpeg4");
        await Assert.ThrowsAsync<IOException>(() => export.ExportAsync(project, projectDirectory, null, default, new() { Directory = multi }));
        string editing = Path.Combine(projectDirectory, "exports", "editing");
        await export.ExportAsync(project, projectDirectory, null, default, new() { Directory = editing, Profile = "Editing package" });
        Assert.True(File.Exists(Path.Combine(editing, "dialogue.wav"))); Assert.False(File.Exists(Path.Combine(editing, "dubbed.mkv")));
        var auto = new AutoDubService(import, new TranslationService(registry), speech, export, store);
        string autoOutput = Path.Combine(projectDirectory, "exports", "auto");
        var autoResult = await auto.RunAsync(project, video, projectDirectory, new() { Directory = autoOutput }, null, default);
        Assert.True(File.Exists(Path.Combine(autoOutput, "dubbed.mp4")));
        Assert.Equal(project.Segments[0].Translation, autoResult.Segments[0].Translation);
        string frame = Path.Combine(projectDirectory, "frame.png");
        await Media.ExtractFrameAsync(mp4, frame, TimeSpan.FromSeconds(1), default);
        Assert.True(new FileInfo(frame).Length > 0);
    }

    [FFmpegFact]
    [Trait("Requires", "FFMPEG")]
    public async Task MixPreservesSilenceOffsetsAndSimultaneousSpeakers()
    {
        Directory.CreateDirectory(directory);
        string a = Path.Combine(directory, "a.wav"), b = Path.Combine(directory, "b.wav");
        await new MockTtsEngine().SynthesizeAsync(new() { Text = "a", Language = "es", OutputPath = a, TargetDuration = TimeSpan.FromSeconds(0.5) }, null, CancellationToken.None);
        await new MockTtsEngine("second", "second", 660).SynthesizeAsync(new() { Text = "b", Language = "es", OutputPath = b, TargetDuration = TimeSpan.FromSeconds(0.5) }, null, CancellationToken.None);
        string mix = Path.Combine(directory, "mix.wav");
        await Media.MixAsync(new([new(a, TimeSpan.FromSeconds(1)), new(b, TimeSpan.FromSeconds(1.25))], mix,
            TimeSpan.FromSeconds(2), 16000, 1), null, CancellationToken.None);
        string raw = Path.Combine(directory, "mix.pcm");
        var decode = await runner.RunAsync(new(FFmpeg, ["-v", "error", "-i", mix, "-f", "s16le", "-acodec", "pcm_s16le", raw]), CancellationToken.None);
        Assert.Equal(0, decode.ExitCode);
        var bytes = await File.ReadAllBytesAsync(raw);
        double Energy(double start) => Math.Sqrt(Enumerable.Range((int)(start * 16000), 1600)
            .Select(i => Math.Pow(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)), 2)).Average());
        Assert.InRange(Energy(0.2), 0, 1);
        Assert.InRange(Energy(1.05), 1900, 2200);
        Assert.True(Energy(1.3) > Energy(1.05) * 1.3);
        Assert.InRange(Energy(1.85), 0, 1);
        string cut = Path.Combine(directory, "cut.wav");
        await Media.CutAsync(mix, cut, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.25), CancellationToken.None);
        Assert.InRange((await Media.ProbeAsync(cut, CancellationToken.None)).Duration.TotalSeconds, 0.249, 0.251);
    }

    [FFmpegFact]
    [Trait("Requires", "FFMPEG")]
    public async Task ProcessCancellationAndTimeoutStopInfiniteWork()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var request = new ProcessRequest(FFmpeg, ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "anullsrc", "-f", "null", "-"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(request, cancel.Token));
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(request with { Timeout = TimeSpan.FromMilliseconds(500) }, CancellationToken.None));
    }

    [FFmpegFact]
    [Trait("Requires", "FFMPEG")]
    public async Task FailedMediaWritePreservesExistingOutput()
    {
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "keep.wav");
        await File.WriteAllTextAsync(output, "preserve me");
        await Assert.ThrowsAsync<MediaProcessingException>(() => Media.ExtractAudioAsync(Path.Combine(directory, "missing.mp4"), output, 16000, 1, null, CancellationToken.None));
        Assert.Equal("preserve me", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
