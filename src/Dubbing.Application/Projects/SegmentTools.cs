using Dubbing.Core.Projects;
using Dubbing.Core.Pipeline;

namespace Dubbing.Application.Projects;

public static class SegmentTools
{
    public static DubbingProject Delete(DubbingProject project, string id)
    {
        Require(project, id);
        return Replace(project, [id], []);
    }
    public static DubbingProject MergeNext(DubbingProject project, string id)
    {
        var ordered = project.Segments.OrderBy(s => s.Start).ToArray();
        int index = Array.FindIndex(ordered, s => s.Id == id);
        if (index < 0 || index + 1 >= ordered.Length) throw new InvalidOperationException("There is no next segment to merge.");
        var a = ordered[index]; var b = ordered[index + 1];
        string translation = a.Translation.Language == project.Settings.TargetLanguage && b.Translation.Language == project.Settings.TargetLanguage
            && !string.IsNullOrWhiteSpace(a.Translation.Text) && !string.IsNullOrWhiteSpace(b.Translation.Text)
            && !a.DirtyStages.Contains(PipelineStage.Translation) && !b.DirtyStages.Contains(PipelineStage.Translation)
            ? Join(a.Translation.Text, b.Translation.Text) : "";
        var merged = Fresh(project, a, a.Start, a.End > b.End ? a.End : b.End, Join(a.Source.Text, b.Source.Text), translation) with { Id = a.Id };
        return Replace(project, [a.Id, b.Id], [merged]);
    }
    public static DubbingProject Split(DubbingProject project, string id, TimeSpan at, string first, string second, string firstTranslation, string secondTranslation)
    {
        var old = Require(project, id);
        Validate(project, old.Start, at); Validate(project, at, old.End);
        var a = Fresh(project, old, old.Start, at, first, firstTranslation);
        var b = Fresh(project, old, at, old.End, second, secondTranslation);
        return Replace(project, [id], [a, b]);
    }
    public static DubbingProject Create(DubbingProject project, TimeSpan start, TimeSpan end, string original, string translation, string? speaker)
    {
        Validate(project, start, end);
        if (FindCreateOverlaps(project.Segments, start, end).Count > 0)
            throw new ArgumentException("The new segment overlaps an existing segment. Choose an empty gap.");
        if (speaker is not null && !project.Speakers.Any(s => s.Id == speaker)) throw new ArgumentException("Choose an available speaker.");
        return Replace(project, [], [Fresh(project, new DubSegment { SpeakerId = speaker, Emotion = new() { Mode = EmotionMode.SourceAudio } }, start, end, original, translation)]);
    }
    // Half-open slots: touching boundaries are valid; even contained ranges are conflicts.
    // Include excluded segments: their timeline slots still exist and must remain visible.
    public static IReadOnlyList<int> FindCreateOverlaps(IReadOnlyList<DubSegment> segments, TimeSpan start, TimeSpan end) =>
        segments.Select((segment, index) => (segment, index)).Where(x => start < x.segment.End && end > x.segment.Start)
            .Select(x => x.index + 1).ToArray();

    public static void Validate(DubbingProject project, TimeSpan start, TimeSpan end)
    {
        if (project.Source is null || start < TimeSpan.Zero || end > project.Source.Duration || end - start < TimeSpan.FromMilliseconds(100))
            throw new ArgumentException("Choose a range inside the video, at least 0.1 seconds long.");
    }
    private static DubSegment Require(DubbingProject project, string id) => project.Segments.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException(id);
    private static string Join(string a, string b) => string.Join(" ", new[] { a.Trim(), b.Trim() }.Where(t => t.Length > 0));
    private static DubSegment Fresh(DubbingProject project, DubSegment template, TimeSpan start, TimeSpan end, string original, string translation)
    {
        bool translated = !string.IsNullOrWhiteSpace(translation);
        return template with { Id = Guid.NewGuid().ToString("N"), Start = start, End = end,
            Source = new(project.Settings.SourceLanguage, original.Trim()), Translation = new(project.Settings.TargetLanguage, translation.Trim(), translated),
            Words = [], GeneratedSpeech = null, FittedSpeech = null, SourceReferenceStart = null, SourceReferenceEnd = null,
            ClipAudioToSlot = false, ExcludedFromDubbing = false, ExclusionReason = null, Status = translated ? SegmentStatus.TtsPending : SegmentStatus.Analyzed,
            DirtyStages = translated ? [PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux]
                : [PipelineStage.Translation, PipelineStage.Tts, PipelineStage.DurationFit, PipelineStage.Mix, PipelineStage.Mux] };
    }
    private static DubbingProject Replace(DubbingProject project, string[] removed, DubSegment[] added) => project with
    {
        Segments = project.Segments.Where(s => !removed.Contains(s.Id)).Concat(added).OrderBy(s => s.Start).ToArray(),
        // Keep extracted reference files usable, but detach references from obsolete segment identities.
        Speakers = project.Speakers.Select(s => removed.Contains(s.VoiceReferenceSegmentId) ? s with { VoiceReferenceSegmentId = null } : s).ToArray()
    };
}
