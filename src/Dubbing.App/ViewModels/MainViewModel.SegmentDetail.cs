using CommunityToolkit.Mvvm.Input;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyList<string> OutputLanguages { get; } = ["zh", "en", "ja", "es", "ar"];
    public string SelectedSegmentTitle => SelectedSegment is null ? L["No segment selected"] :
        L.Format("Segment {0} selected", Segments.IndexOf(SelectedSegment) + 1);

    [RelayCommand] private Task ModifyDetailAsync() => RunAsync(async ct =>
    {
        if (SelectedSegment is null || workspace is null || session.DirectoryPath is null) return;
        StopRequested?.Invoke();
        var editor = new SegmentEditorViewModel(); editor.CopyFrom(Editor);
        string source = SpeakerSourcePath();
        double duration = (await workspace.Media.ProbeAsync(source, ct)).Duration.TotalSeconds;
        double start = Math.Max(0, editor.StartSeconds - 5), end = Math.Min(duration, editor.EndSeconds + 5);
        if (end <= start) throw new InvalidOperationException(L["Segment range must stay inside the video."]);
        string scratch = Path.Combine(session.DirectoryPath, "cache", "segment-editor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previews = new List<Task>();
        try
        {
            string context = Path.Combine(scratch, "context.wav");
            await workspace.Media.CutAsync(source, context, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end-start), ct);
            var peaks = await workspace.Media.WaveformAsync(context, 1400, ct);
            async Task Preview(double a, double b)
            {
                StopRequested?.Invoke();
                string file = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".wav");
                await workspace.Media.CutAsync(source, file, TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b-a), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                TrackPlayback = false; DubPreviewPath = null;
                PlaybackRequested?.Invoke(file, TimeSpan.Zero);
            }
            var model = new SegmentDetailViewModel(SelectedSegmentTitle, editor, Speakers.ToArray(), VoiceEngines, peaks, start, end, duration,
                (a,b) => { var task = Preview(a,b); previews.Add(task); return task; }, () => { cancellation.Cancel(); StopRequested?.Invoke(); });
            if (await dialogs.EditSegmentAsync(model))
            {
                Editor.CopyFrom(editor);
                ApplySegmentCore();
                await ResolveCollisionsAsync(ct);
                await session.SaveAsync(null, ct);
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await Task.WhenAll(previews); } catch (Exception) { }
            StopRequested?.Invoke(); Directory.Delete(scratch, true);
        }
    });
}
