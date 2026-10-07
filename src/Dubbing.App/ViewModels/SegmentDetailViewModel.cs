using Dubbing.Core.Projects;
using Dubbing.Core.Engines;

namespace Dubbing.App.ViewModels;

public sealed record SegmentDetailViewModel(string Title, SegmentEditorViewModel Editor,
    IReadOnlyList<SpeakerProfile> Speakers, IReadOnlyList<EngineDescriptor> VoiceEngines,
    IReadOnlyList<float> Peaks, double WindowStart, double WindowEnd, double VideoDuration,
    Func<double,double,Task> Preview, Action Stop);
