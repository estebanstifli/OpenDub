using Dubbing.Application.Playback;
using Dubbing.Application.Pipeline;
using Dubbing.Application.Projects;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    public event Func<RealtimePlaybackPlan, TimeSpan, Task>? RealtimePlaybackRequested;
    public event Func<RealtimePlaybackPlan, Task>? RealtimeMixChanged;
    private RealtimeAudioService? realtimeAudio;
    private bool resolvingCollisions;
    private int realtimeMixVersion;
    private async Task<bool> ResolveCollisionsAsync(CancellationToken ct)
    {
        if (resolvingCollisions) return false;
        resolvingCollisions = true;
        CollisionChoice? repeat = null;
        var merged = new HashSet<string>();
        try
        {
            while (true)
            {
                var collision = SegmentCollisions.Find(session.Project);
                if (collision is null)
                {
                    var pending = session.Project.Segments.Where(s => merged.Contains(s.Id)).ToArray();
                    if (pending.Length == 0) return true;
                    if (pending.Any(s => s.DirtyStages.Contains(Dubbing.Core.Pipeline.PipelineStage.Translation) || string.IsNullOrWhiteSpace(s.Translation.Text)))
                    { Status = L["Segments merged. Complete the translation, then generate speech."]; return false; }
                    if (!await SaveCoreAsync(ct)) return false;
                    StopRequested?.Invoke();
                    Status = L["Generating the merged dialogue…"];
                    try { await speech.GenerateAsync(session.Project, session.DirectoryPath!, ReportProgress(), ct, fitDuration: false, segmentIds: pending.Select(s => s.Id).ToHashSet(), overwriteExisting: true); }
                    finally { await session.OpenAsync(session.DirectoryPath!, CancellationToken.None); Refresh(); }
                    merged.Clear();
                    continue; // Check the measured duration of the newly synthesized voice too.
                }
                ct.ThrowIfCancellationRequested(); PauseRequested?.Invoke();
                var decision = repeat is { } choice && (choice != CollisionChoice.Limit || collision.Second.Start > collision.First.Start)
                    ? new CollisionDecision(choice, true) : await dialogs.ResolveCollisionAsync(collision);
                if (decision.Choice == CollisionChoice.Cancel) return false;
                repeat = decision.ApplyToAll ? decision.Choice : null;
                session.Update(SegmentCollisions.Resolve(session.Project, collision, decision.Choice));
                Refresh();
                if (session.DirectoryPath is not null) await session.SaveAsync(null, ct);
                if (decision.Choice == CollisionChoice.Merge) merged.Add(collision.First.Id);
            }
        }
        finally { resolvingCollisions = false; }
    }
    private async Task<RealtimePlaybackPlan> PlaybackPlanAsync(CancellationToken ct, AudioAudition audition = AudioAudition.Mix)
    {
        realtimeAudio ??= new RealtimeAudioService(workspace!.Media);
        return await realtimeAudio.PrepareAsync(session.Project, session.DirectoryPath!, ReportProgress(), ct, audition);
    }
    private async Task UpdateRealtimeMixAsync()
    {
        if (workspace is null || session.DirectoryPath is null || RealtimeMixChanged is null) return;
        int version = ++realtimeMixVersion;
        try
        {
            var plan = await PlaybackPlanAsync(CancellationToken.None);
            if (version == realtimeMixVersion) await RealtimeMixChanged(plan);
        }
        catch (Exception ex) { Status = L.Format("Playback unavailable: {0}", ex.Message); }
    }
}
