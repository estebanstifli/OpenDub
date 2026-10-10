using CommunityToolkit.Mvvm.Input;
using Dubbing.App.Services;
using Dubbing.Application.Projects;
using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    public bool CanChangeSegment => !IsBusy && SelectedSegment is not null;
    public bool CanCreateSegment => !IsBusy && session.Project.Source is not null;
    public bool CanMergeSegment => CanChangeSegment && session.Project.Segments.OrderBy(s=>s.Start).LastOrDefault()?.Id != SelectedSegment?.Id;
    private void NotifySegmentTools()
    {
        DeleteSegmentCommand.NotifyCanExecuteChanged(); MergeNextSegmentCommand.NotifyCanExecuteChanged();
        SplitSegmentCommand.NotifyCanExecuteChanged(); CreateSegmentCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute=nameof(CanChangeSegment))] private Task DeleteSegmentAsync() => RunAsync(async ct =>
    {
        var selected = SelectedSegment!;
        if (!await dialogs.ConfirmAsync(L.Format("Delete segment {0}? Its text and audio placement will be removed. Audio files will be kept.", selected.Number))) return;
        if (!await SaveCoreAsync(ct)) return;
        StopPlayback();
        await CommitSegmentToolsAsync(SegmentTools.Delete(session.Project, selected.Id), [], ct);
        Status=L["Segment deleted."];
    });
    [RelayCommand(CanExecute=nameof(CanMergeSegment))] private Task MergeNextSegmentAsync() => RunAsync(async ct =>
    {
        var selected=SelectedSegment!;
        if (!await dialogs.ConfirmAsync(L.Format("Merge segment {0} with the next segment? Texts will be joined using the first speaker. Generate new speech afterwards.",selected.Number))) return;
        if (!await SaveCoreAsync(ct)) return;
        StopPlayback();
        await CommitSegmentToolsAsync(SegmentTools.MergeNext(session.Project,selected.Id), [selected.Id],ct);
        Status=L["Segments merged. Review the text and generate new speech."];
    });
    [RelayCommand(CanExecute=nameof(CanChangeSegment))] private Task SplitSegmentAsync() => RunAsync(ct=>EditSegmentRangeCoreAsync(true,ct));
    [RelayCommand(CanExecute=nameof(CanCreateSegment))] private Task CreateSegmentAsync() => RunAsync(ct=>EditSegmentRangeCoreAsync(false,ct));
    private async Task CommitSegmentToolsAsync(DubbingProject project, HashSet<string> ids, CancellationToken ct)
    {
        session.Update(project); await session.SaveAsync(null,ct); Refresh(); NotifySegmentTools();
        SelectedSegment=Segments.FirstOrDefault(s=>ids.Contains(s.Id)) ?? Segments.FirstOrDefault();
        // The preview reads the updated segment list; no full-film WAV is rendered.
        await ResolveAffectedCollisionsAsync(ct,ids);
    }
    private async Task EditSegmentRangeCoreAsync(bool split, CancellationToken ct)
    {
        if (workspace is null || session.Project.Source is null || split && SelectedSegment is null) return;
        if (!await SaveCoreAsync(ct)) return;
        var project=session.Project;
        var old=split ? project.Segments.Single(s=>s.Id==SelectedSegment!.Id) : null;
        double duration=project.Source!.Duration.TotalSeconds;
        double start=old?.Start.TotalSeconds ?? Math.Clamp(Playhead,0,Math.Max(0,duration-.1));
        double end=old?.End.TotalSeconds ?? Math.Min(duration,start+5);
        if (split && end-start<.2) throw new InvalidOperationException(L["The segment must be at least 0.2 seconds long to split."]);
        double point=(start+end)/2;
        static (string,string) Divide(string value)
        {
            var words=value.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(words.Length>1) { int n=(words.Length+1)/2; return (string.Join(" ",words.Take(n)),string.Join(" ",words.Skip(n))); }
            int mid=value.Length/2; return (value[..mid],value[mid..]);
        }
        var original=old is null ? ("", "") : Divide(old.Source.Text);
        if(old?.Words.Count>1)
            original=(string.Join(" ",old.Words.Where(w=>w.Start<TimeSpan.FromSeconds(point)).Select(w=>w.Text)),
                string.Join(" ",old.Words.Where(w=>w.Start>=TimeSpan.FromSeconds(point)).Select(w=>w.Text)));
        var translated=old is not null && old.Translation.Language==project.Settings.TargetLanguage && !old.DirtyStages.Contains(Dubbing.Core.Pipeline.PipelineStage.Translation)
            ? Divide(old.Translation.Text) : ("", "");
        double resumePosition=Playhead; StopPlayback(); Playhead=resumePosition; string source=SpeakerSourcePath();
        string scratch=Path.Combine(session.DirectoryPath!,"cache","segment-tools",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        bool usedAsr = false;
        try
        {
            var peaks=await workspace.Media.WaveformAsync(source,10000,ct);
            async Task<string> Transcribe(double a,double b,CancellationToken token)
            {
                string file=Path.Combine(scratch,Guid.NewGuid().ToString("N")+".wav");
                await workspace.Media.CutAsync(source,file,TimeSpan.FromSeconds(a),TimeSpan.FromSeconds(b-a),token);
                var engine=registry.GetEngine<ITranscriptionEngine>(project.Engines.Transcription ?? throw new InvalidOperationException("Select a transcription engine."));
                usedAsr = true;
                var result=await engine.TranscribeAsync(new(new(file,await ContentFingerprint.ComputeAsync(file,token)),project.Settings.SourceLanguage,TimeSpan.FromSeconds(b-a)),ReportProgress(),token);
                return result.Text;
            }
            async Task Preview(double a,double b,CancellationToken token)
            {
                StopRequested?.Invoke();
                string file=Path.Combine(scratch,Guid.NewGuid().ToString("N")+".wav");
                await workspace.Media.CutAsync(source,file,TimeSpan.FromSeconds(a),TimeSpan.FromSeconds(b-a),token); token.ThrowIfCancellationRequested();
                TrackPlayback=false; DubPreviewPath=null; PlaybackRequested?.Invoke(file,TimeSpan.Zero);
            }
            var result=await dialogs.EditSegmentRangeAsync(new(split,duration,peaks,
                new(start,end,point,original.Item1,original.Item2,translated.Item1,translated.Item2,old?.SpeakerId ?? project.Segments.FirstOrDefault(s=>s.Id==SelectedSegment?.Id)?.SpeakerId),
                project.Speakers,Transcribe,Preview,()=>StopRequested?.Invoke()),ct);
            ct.ThrowIfCancellationRequested(); if(result is null) return;
            var changed=split ? SegmentTools.Split(project,old!.Id,TimeSpan.FromSeconds(result.Split),result.First,result.Second,result.FirstTranslation,result.SecondTranslation)
                : SegmentTools.Create(project,TimeSpan.FromSeconds(result.Start),TimeSpan.FromSeconds(result.End),result.First,result.FirstTranslation,result.SpeakerId);
            var ids=changed.Segments.Where(s=>project.Segments.All(o=>o.Id!=s.Id)).Select(s=>s.Id).ToHashSet();
            if(split) changed=changed with { Segments=changed.Segments.Select(s=>ids.Contains(s.Id)?s with { SpeakerId=result.SpeakerId }:s).ToArray() };
            await CommitSegmentToolsAsync(changed,ids,ct);
            Status=L[split ? "Segment split. Review both parts and generate new speech." : "Segment created. Translate and generate speech when ready."];
        }
        finally { StopRequested?.Invoke(); try { if (usedAsr) await workspace.Runtimes.ReleaseAsync(); } finally { Directory.Delete(scratch,true); TrackPlayback=true; OnPropertyChanged(nameof(VideoPath)); } }
    }
}
