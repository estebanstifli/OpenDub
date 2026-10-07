namespace Dubbing.Core.Pipeline;

public enum PipelineStage { Import, Probe, ExtractAudio, Separation, Transcription, Diarization,
    Alignment, Timeline, VoiceReference, Translation, Tts, DurationFit, Mix, Mux }

public static class StageDependencies
{
    private static readonly IReadOnlyDictionary<PipelineStage, PipelineStage[]> Dependents =
        new Dictionary<PipelineStage, PipelineStage[]>
        {
            [PipelineStage.Import] = [PipelineStage.Probe],
            [PipelineStage.Probe] = [PipelineStage.ExtractAudio],
            [PipelineStage.ExtractAudio] = [PipelineStage.Separation, PipelineStage.Transcription, PipelineStage.Diarization],
            [PipelineStage.Separation] = [PipelineStage.Transcription, PipelineStage.Diarization, PipelineStage.VoiceReference, PipelineStage.Mix],
            [PipelineStage.Transcription] = [PipelineStage.Alignment, PipelineStage.Timeline],
            [PipelineStage.Diarization] = [PipelineStage.Timeline, PipelineStage.VoiceReference],
            [PipelineStage.Alignment] = [PipelineStage.Timeline],
            [PipelineStage.Timeline] = [PipelineStage.VoiceReference, PipelineStage.Translation],
            [PipelineStage.VoiceReference] = [PipelineStage.Tts],
            [PipelineStage.Translation] = [PipelineStage.Tts],
            [PipelineStage.Tts] = [PipelineStage.DurationFit],
            [PipelineStage.DurationFit] = [PipelineStage.Mix],
            [PipelineStage.Mix] = [PipelineStage.Mux]
        };

    public static IReadOnlySet<PipelineStage> After(PipelineStage changedStage)
    {
        var result = new HashSet<PipelineStage>();
        Visit(changedStage);
        return result;
        void Visit(PipelineStage stage)
        {
            if (!Dependents.TryGetValue(stage, out var next)) return;
            foreach (var dependent in next)
                if (result.Add(dependent)) Visit(dependent);
        }
    }
}
