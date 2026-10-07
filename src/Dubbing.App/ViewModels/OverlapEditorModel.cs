using Dubbing.Application.Pipeline;
using Dubbing.Application.Playback;
using Dubbing.Application.Projects;
using Dubbing.Core.Media;
using Dubbing.Core.Pipeline;
using Dubbing.Core.Projects;

namespace Dubbing.App.ViewModels;

public sealed record OverlapWaveSlice(double Start, double End, IReadOnlyList<float> Peaks);
public sealed class OverlapEditorModel : IDisposable
{
    private readonly IMediaEngine media;
    private readonly string directory, scratch;
    private readonly Func<DubbingProject, string?, CancellationToken, Task<DubbingProject>> commit;
    private readonly Func<DubbingProject> savedProject;
    private readonly RealtimeAudioService playback;
    private readonly Dictionary<string, IReadOnlyList<float>> peaks = [];
    private string? originalKey;
    public SegmentCollision Collision { get; }
    public DubbingProject Project { get; private set; }
    public string SelectedId { get; set; }
    public HashSet<string> ChangedIds { get; } = [];
    public bool HasDraft { get; private set; }
    public double WindowStart { get; private set; }
    public double WindowEnd { get; private set; }
    public IReadOnlyList<OverlapWaveSlice> OriginalWaves { get; private set; } = [];
    public IReadOnlyList<OverlapWaveSlice> DubWaves { get; private set; } = [];
    public DubSegment Selected => Project.Segments.Single(s => s.Id == SelectedId);
    public IReadOnlyList<DubSegment> Ordered => Project.Segments.Where(s => !s.ExcludedFromDubbing).OrderBy(s => s.Start).ToArray();
    public IReadOnlyList<DubSegment> Context
    {
        get
        {
            var all = Ordered.ToArray(); int i = Array.FindIndex(all, s => s.Id == SelectedId);
            var neighbours = all.Skip(Math.Max(0, i - 1)).Take(i == 0 ? 2 : 3).ToList();
            // Long clips may overlap a non-immediate neighbour; keep that partner visible.
            var collision = CurrentCollision;
            if (collision is not null)
                foreach (var s in new[] { collision.First, collision.Second })
                    if (neighbours.All(n => n.Id != s.Id)) neighbours.Add(s);
            return neighbours.OrderBy(s => s.Start).ToArray();
        }
    }
    public SegmentCollision? CurrentCollision => SegmentCollisions.Find(Project, new HashSet<string> { SelectedId });
    public int Number(DubSegment segment) => Project.Segments.ToList().FindIndex(s => s.Id == segment.Id) + 1;
    public OverlapEditorModel(DubbingProject project, SegmentCollision collision, string directory, IMediaEngine media,
        Func<DubbingProject, string?, CancellationToken, Task<DubbingProject>> commit, Func<DubbingProject> savedProject)
    {
        Project = project; Collision = collision; SelectedId = collision.First.Id; this.directory = directory;
        this.media = media; this.commit = commit; this.savedProject = savedProject; playback = new(media);
        scratch = Path.Combine(directory, "cache", "overlap-editor", Guid.NewGuid().ToString("N"));
        UpdateBounds();
    }
    public void Edit(double start, double end, string original, string translation)
    {
        var old = Selected;
        if (old.Start.TotalSeconds == start && old.End.TotalSeconds == end && old.Source.Text == original && old.Translation.Text == translation) return;
        Project = OverlapEditing.Edit(Project, SelectedId, start, end, original, translation);
        HasDraft = true; ChangedIds.Add(SelectedId);
    }
    public void UpdateBounds()
    {
        var context = Context;
        WindowStart = Math.Max(0, context.Min(s => s.Start.TotalSeconds) - 1);
        WindowEnd = Math.Min(Project.Source?.Duration.TotalSeconds ?? double.MaxValue,
            context.Max(s => Math.Max(s.End.TotalSeconds, s.Start.TotalSeconds + ProjectAudioMix.EffectiveDuration(s).TotalSeconds)) + 1);
    }
    public async Task LoadWavesAsync(CancellationToken ct)
    {
        UpdateBounds(); Directory.CreateDirectory(scratch);
        string? original = Project.Stems.TryGetValue("vocals", out var voice) ? voice.Path : Project.SelectedSourceAudio?.Path ?? Project.Source?.Path;
        if (original is not null)
        {
            original = Path.GetFullPath(Path.Combine(directory, original));
            if (File.Exists(original))
            {
                var info = new FileInfo(original);
                string key = $"{original}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{WindowStart:R}|{WindowEnd:R}";
                if (originalKey != key)
                {
                    string clip = Path.Combine(scratch, "original.wav");
                    await media.CutAsync(original, clip, TimeSpan.FromSeconds(WindowStart), TimeSpan.FromSeconds(WindowEnd - WindowStart), ct);
                    OriginalWaves = [new(WindowStart, WindowEnd, await media.WaveformAsync(clip, 1600, ct))]; originalKey = key;
                }
            }
        }
        var dub = new List<OverlapWaveSlice>();
        foreach (var segment in Context)
        {
            var speech = ProjectAudioMix.EffectiveSpeech(segment); if (speech is null) continue;
            string path = Path.Combine(directory, speech.File); if (!File.Exists(path)) continue;
            var info = new FileInfo(path); string key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (!peaks.TryGetValue(key, out var values)) peaks[key] = values = await media.WaveformAsync(path, 1000, ct);
            double duration = ProjectAudioMix.EffectiveDuration(segment).TotalSeconds;
            // Preserve the waveform's time scale when a clip has an explicit trim cap.
            if (duration < speech.Duration.TotalSeconds) values = values.Take(Math.Max(1, (int)Math.Round(values.Count * duration / speech.Duration.TotalSeconds))).ToArray();
            dub.Add(new(segment.Start.TotalSeconds, segment.Start.TotalSeconds + duration, values));
        }
        DubWaves = dub;
    }
    public Task<RealtimePlaybackPlan> PlaybackAsync(bool dub, CancellationToken ct) => playback.PrepareAsync(
        Project with { Segments = Context, Settings = Project.Settings with { VoiceGain = 1, OriginalVoiceGain = 1 } },
        directory, null, ct, dub ? AudioAudition.Dub : AudioAudition.OriginalVoice);
    public async Task SaveAsync(bool regenerate, CancellationToken ct)
    {
        if (regenerate && (string.IsNullOrWhiteSpace(Selected.Translation.Text) || Selected.Translation.Language != Project.Settings.TargetLanguage))
            throw new InvalidOperationException("Translate the selected segment before regenerating speech.");
        ChangedIds.Add(SelectedId);
        try { Project = await commit(Project, regenerate ? SelectedId : null, ct); }
        finally { Project = savedProject(); HasDraft = false; }
    }
    public async Task MergeAsync(bool previous, CancellationToken ct)
    {
        var all = Ordered.ToArray(); int i = Array.FindIndex(all, s => s.Id == SelectedId);
        int other = i + (previous ? -1 : 1);
        if (other < 0 || other >= all.Length) throw new InvalidOperationException("No neighbouring segment is available.");
        var a = all[Math.Min(i, other)]; var b = all[Math.Max(i, other)];
        Project = OverlapEditing.Merge(Project, a.Id, b.Id); SelectedId = a.Id;
        ChangedIds.Add(a.Id); ChangedIds.Add(b.Id); HasDraft = true;
        bool canGenerate = !string.IsNullOrWhiteSpace(Selected.Translation.Text) && !Selected.DirtyStages.Contains(PipelineStage.Translation);
        await SaveAsync(canGenerate, ct);
    }
    public async Task ResolveAsync(CollisionChoice choice, CancellationToken ct)
    {
        var collision = CurrentCollision ?? throw new InvalidOperationException("No overlap remains for this segment.");
        Project = SegmentCollisions.Resolve(Project, collision, choice);
        ChangedIds.Add(collision.First.Id); ChangedIds.Add(collision.Second.Id); HasDraft = true;
        await SaveAsync(false, ct);
    }
    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
