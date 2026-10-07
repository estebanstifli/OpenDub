using Dubbing.Application.Media;
using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class VoiceReferenceSelector(IMediaEngine media)
{
    public IReadOnlyList<DubSegment> Rank(DubbingProject project, string speakerId) => project.Segments
        .Where(s => s.SpeakerId == speakerId && s.TargetDuration >= TimeSpan.FromSeconds(1))
        .Where(s => !project.Segments.Any(other => other.Id != s.Id && other.SpeakerId != speakerId && other.Start < s.End && other.End > s.Start))
        .OrderByDescending(s => Math.Min(s.TargetDuration.TotalSeconds, 10))
        .ThenByDescending(s => s.Words.Count == 0 ? 0 : s.Words.Average(w => w.Confidence ?? 0)).ToArray();
    public async Task<DubbingProject> SelectAsync(DubbingProject project, string directory, CancellationToken ct)
    {
        var speakers = new List<SpeakerProfile>();
        string audio = project.Stems.TryGetValue("vocals", out var vocals) ? Path.Combine(directory, vocals.Path) : Path.Combine(directory, project.SelectedSourceAudio?.Path ?? "assets/extracted/master.wav");
        foreach (var speaker in project.Speakers)
        {
            ct.ThrowIfCancellationRequested();
            if (speaker.VoiceReference is not null) { speakers.Add(speaker); continue; }
            var candidate = project.Segments.Where(s => s.SpeakerId == speaker.Id && !s.ExcludedFromDubbing && s.TargetDuration >= TimeSpan.FromSeconds(1)).OrderBy(s => s.Start).FirstOrDefault();
            if (candidate is null) { speakers.Add(speaker); continue; }
            string file = $"assets/voices/{Guid.NewGuid():N}.wav";
            await media.CutAsync(audio, Path.Combine(directory, file), candidate.Start, TimeSpan.FromSeconds(Math.Min(10, candidate.TargetDuration.TotalSeconds)), ct);
            speakers.Add(speaker with { VoiceReference = new(file, await ContentFingerprint.ComputeAsync(Path.Combine(directory, file), ct)), VoiceReferenceIsAutomatic = true, VoiceReferenceSegmentId = candidate.Id });
        }
        return project with { Speakers = speakers };
    }
}
