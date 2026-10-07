using Dubbing.Core.Engines;
using Dubbing.Core.Media;
using Dubbing.Core.Projects;

namespace Dubbing.Application.Pipeline;

public sealed class DubRenderService(IMediaEngine media)
{
    public async Task<string> RenderAsync(DubbingProject project, string directory,
        IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        var source = project.Source ?? throw new InvalidOperationException("Import a video first.");
        if (project.Segments.Count == 0) throw new InvalidOperationException("There are no segments to render.");
        var clips = await ProjectAudioMix.ClipsAsync(project, directory, true, progress, cancellationToken);
        string output = Path.Combine(directory, "renders", "dub_track.wav");
        await ProjectAudioMix.RenderAsync(media, clips, output, source.Duration, source.SampleRate,
            ProjectAudioMix.Channels(project), progress, cancellationToken);
        return output;
    }
}
