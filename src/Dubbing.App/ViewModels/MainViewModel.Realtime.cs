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
    private readonly HashSet<string> pendingTimingReviews = [];
    private string? timingReviewProject;
    private void ResetTimingReviewsForProject()
    {
        if (timingReviewProject == session.Project.Id) return;
        pendingTimingReviews.Clear(); timingReviewProject = session.Project.Id;
    }
    private Task<bool> ReviewEditedTimingAsync(CancellationToken ct)
    {
        ResetTimingReviewsForProject();
        return pendingTimingReviews.Count == 0 ? Task.FromResult(true) : ResolveAffectedCollisionsAsync(ct, [.. pendingTimingReviews]);
    }
    private Task<bool> ResolveCollisionsAsync(CancellationToken ct) => ResolveAffectedCollisionsAsync(ct, null);
    private async Task<bool> ReviewPlaybackOverlapsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (SegmentCollisions.Find(session.Project) is null) return true;
        PauseRequested?.Invoke();
        var choice = await dialogs.ConfirmPlaybackOverlapsAsync();
        ct.ThrowIfCancellationRequested();
        // Playback-only bypass: do not accept collisions permanently or change export validation.
        return choice switch
        {
            Services.PlaybackOverlapChoice.IgnoreAndPlay => true,
            Services.PlaybackOverlapChoice.Review => await ResolveCollisionsAsync(ct),
            _ => false
        };
    }
    private async Task<bool> ResolveAffectedCollisionsAsync(CancellationToken ct, HashSet<string>? affectedIds)
    {
        if (resolvingCollisions) return false;
        ResetTimingReviewsForProject();
        // A cancelled review is retried on playback/export or the next timing change,
        // not on every subsequent text edit or unrelated autosave.
        if (affectedIds is null) pendingTimingReviews.Clear();
        else pendingTimingReviews.ExceptWith(affectedIds);
        resolvingCollisions = true;
        CollisionChoice? repeat = null;
        var merged = new HashSet<string>();
        try
        {
            while (true)
            {
                var collision = SegmentCollisions.Find(session.Project, affectedIds);
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
                    ? new CollisionDecision(choice, true) : await EditOverlapAsync(collision, affectedIds, ct);
                if (decision.Choice == CollisionChoice.Cancel) return false;
                if (decision.Choice == CollisionChoice.Edited) { repeat = null; continue; }
                repeat = decision.ApplyToAll ? decision.Choice : null;
                session.Update(SegmentCollisions.Resolve(session.Project, collision, decision.Choice));
                Refresh();
                if (session.DirectoryPath is not null) await session.SaveAsync(null, ct);
                if (decision.Choice == CollisionChoice.Merge) { merged.Add(collision.First.Id); affectedIds?.Add(collision.First.Id); }
            }
        }
        finally { resolvingCollisions = false; }
    }
    private async Task<CollisionDecision> EditOverlapAsync(SegmentCollision collision, HashSet<string>? affectedIds, CancellationToken ct)
    {
        if (workspace is null || session.DirectoryPath is not { } directory) return await dialogs.ResolveCollisionAsync(collision);
        StopRequested?.Invoke();
        async Task<DubbingProject> Commit(DubbingProject draft, string? regenerateId, CancellationToken token)
        {
            session.Update(draft); await session.SaveAsync(directory, token); Refresh();
            if (regenerateId is not null)
            {
                try { await speech.GenerateAsync(session.Project, directory, ReportProgress(), token, regenerateId, fitDuration: true, overwriteExisting: true); }
                finally { await session.OpenAsync(directory, CancellationToken.None); Refresh(); }
            }
            return session.Project;
        }
        using var model = new OverlapEditorModel(session.Project, collision, directory, workspace.Media, Commit, () => session.Project);
        var decision = await dialogs.EditOverlapAsync(model, ct);
        affectedIds?.UnionWith(model.ChangedIds);
        return decision;
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
