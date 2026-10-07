using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.Core.Engines;

namespace Dubbing.App.ViewModels;

public sealed record SpeakerSegmentItem(string Id, string Text, string Timing, string SelectionLabel,
    IAsyncRelayCommand PlayCommand, IAsyncRelayCommand SelectCommand);

public partial class MainViewModel
{
    public ObservableCollection<SpeakerSegmentItem> SpeakerSegments { get; } = [];
    private void RefreshSpeakerSegments()
    {
        SpeakerSegments.Clear();
        if (SelectedSpeaker is null) return;
        foreach (var segment in session.Project.Segments.Where(s => s.SpeakerId == SelectedSpeaker.Id).OrderBy(s => s.Start))
            SpeakerSegments.Add(new(segment.Id, segment.Source.Text, $"{segment.Start:hh\\:mm\\:ss\\.fff} – {segment.End:hh\\:mm\\:ss\\.fff}",
                L[SelectedSpeaker.VoiceReferenceSegmentId == segment.Id ? "Selected reference" : "Use reference"],
                new AsyncRelayCommand(() => PlaySpeakerSegmentAsync(segment.Id)), new AsyncRelayCommand(() => SelectSpeakerSegmentAsync(segment.Id))));
    }
    private string SpeakerSourcePath()
    {
        var project = session.Project;
        return project.Stems.TryGetValue("vocals", out var vocals) ? Path.Combine(session.DirectoryPath!, vocals.Path)
            : project.SelectedSourceAudio is { } source ? Path.Combine(session.DirectoryPath!, source.Path)
            : project.Source?.Path ?? throw new InvalidOperationException("Import a video first.");
    }
    private Task PlaySpeakerSegmentAsync(string id) => RunAsync(async ct =>
    {
        if (workspace is null || session.DirectoryPath is null) return;
        var segment = session.Project.Segments.Single(s => s.Id == id);
        string folder = Path.Combine(session.DirectoryPath, "cache", "speaker-preview"); Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".wav");
        await workspace.Media.CutAsync(SpeakerSourcePath(), output, segment.Start, segment.TargetDuration, ct);
        StopRequested?.Invoke(); DubPreviewPath = null; TrackPlayback = false;
        PlaybackRequested?.Invoke(output, TimeSpan.Zero);
    });
    private Task SelectSpeakerSegmentAsync(string id) => RunAsync(async ct =>
    {
        if (workspace is null || SelectedSpeaker is null || !await SaveCoreAsync(ct)) return;
        var segment = session.Project.Segments.Single(s => s.Id == id && s.SpeakerId == SelectedSpeaker.Id);
        string source = SpeakerSourcePath();
        double duration = (await workspace.Media.ProbeAsync(source, ct)).Duration.TotalSeconds;
        double from = segment.Start.TotalSeconds, to = Math.Min(segment.End.TotalSeconds, from + 10);
        if (SelectedSpeaker.VoiceReferenceSegmentId == id && SelectedSpeaker.VoiceReferenceSourceStart is { } savedStart && SelectedSpeaker.VoiceReferenceSourceEnd is { } savedEnd)
        { from = savedStart; to = savedEnd; }
        // Extend short references using real surrounding audio, bounded by other detected speakers.
        if (to - from < 1)
        {
            double lower = session.Project.Segments.Where(s => s.SpeakerId != segment.SpeakerId && s.End <= segment.Start)
                .Select(s => s.End.TotalSeconds).DefaultIfEmpty(0).Max();
            double upper = session.Project.Segments.Where(s => s.SpeakerId != segment.SpeakerId && s.Start >= segment.End)
                .Select(s => s.Start.TotalSeconds).DefaultIfEmpty(duration).Min();
            double missing = 1.2 - (to-from);
            from = Math.Max(lower, from-missing/2); to = Math.Min(upper, from+1.2);
            from = Math.Max(lower, Math.Min(from, to-1.2));
        }
        double windowStart = Math.Max(0, Math.Min(from, segment.Start.TotalSeconds)-3);
        double windowEnd = Math.Min(duration, Math.Max(to, segment.End.TotalSeconds)+3);
        string scratch = Path.Combine(session.DirectoryPath!, "cache", "reference-editor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var previews = new List<Task>();
        using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            string context = Path.Combine(scratch, "context.wav");
            await workspace.Media.CutAsync(source, context, TimeSpan.FromSeconds(windowStart), TimeSpan.FromSeconds(windowEnd-windowStart), ct);
            var peaks = await workspace.Media.WaveformAsync(context, 1000, ct);
            async Task Preview(double a, double b)
            {
                string output = Path.Combine(scratch, Guid.NewGuid().ToString("N")+".wav");
                await workspace.Media.CutAsync(source, output, TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b-a), previewCancellation.Token);
                previewCancellation.Token.ThrowIfCancellationRequested();
                StopRequested?.Invoke(); DubPreviewPath = null; TrackPlayback = false;
                PlaybackRequested?.Invoke(output, TimeSpan.Zero);
            }
            var range = await dialogs.EditVoiceReferenceAsync(peaks, windowStart, windowEnd, from, to,
                (a,b) => { var task = Preview(a,b); previews.Add(task); return task; },
                () => { StopRequested?.Invoke(); previewCancellation.Cancel(); });
            if (range is not { } chosen) return;
            if (chosen.End-chosen.Start < 1 || chosen.End-chosen.Start > 10 || chosen.Start < 0 || chosen.End > duration)
                throw new InvalidOperationException(L["Reference must be between 1 and 10 seconds."]);
            string relative = $"assets/voices/{Guid.NewGuid():N}.wav";
            await workspace.Media.CutAsync(source, Path.Combine(session.DirectoryPath!, relative), TimeSpan.FromSeconds(chosen.Start),
                TimeSpan.FromSeconds(chosen.End-chosen.Start), ct);
            UpdateVoiceReference(new AudioReference(relative), segment.Id, chosen.Start, chosen.End);
            await session.SaveAsync(null, ct);
        }
        finally
        {
            previewCancellation.Cancel();
            try { await Task.WhenAll(previews); } catch (Exception) { }
            StopRequested?.Invoke();
            Directory.Delete(scratch, true);
        }
    });
}
