using Dubbing.Application.Projects;
using Dubbing.Core.Projects;
using Dubbing.Core.Pipeline;
using Dubbing.Infrastructure.Projects;

namespace Dubbing.Application.Tests;

public class SegmentToolsTests
{
    private static DubbingProject Project() => new() { Source=new("video.mp4","hash",TimeSpan.FromSeconds(20),1,16000), Speakers=[new(){Id="a"},new(){Id="b"}],
        Segments=[new(){Id="one",Start=TimeSpan.FromSeconds(1),End=TimeSpan.FromSeconds(5),SpeakerId="a",Source=new("en","Hello there"),Translation=new("es","Hola amigo"),
            GeneratedSpeech=new("one.wav",TimeSpan.FromSeconds(4),"key"),FittedSpeech=new("fit.wav",TimeSpan.FromSeconds(4),"fit"),DirtyStages=[]},
            new(){Id="two",Start=TimeSpan.FromSeconds(5),End=TimeSpan.FromSeconds(8),SpeakerId="b",Source=new("en","See you"),Translation=new("es","Hasta luego"),DirtyStages=[]}] };
    [Fact] public void SplitCreatesNewIdentitiesAndClearsDerivedSpeech()
    {
        var project=Project(); var updated=SegmentTools.Split(project,"one",TimeSpan.FromSeconds(3),"Hello","there","Hola","amigo");
        Assert.Equal(3,updated.Segments.Count); var a=updated.Segments[0]; var b=updated.Segments[1];
        Assert.Equal(a.End,b.Start); Assert.Equal(project.Segments[0].End,b.End); Assert.NotEqual("one",a.Id); Assert.NotEqual(a.Id,b.Id);
        Assert.All(updated.Segments.Take(2),s=>{Assert.Null(s.GeneratedSpeech);Assert.Null(s.FittedSpeech);Assert.Equal("a",s.SpeakerId);Assert.Contains(PipelineStage.Tts,s.DirtyStages);});
        Assert.Equal(project.Segments[1],updated.Segments[2]); Assert.NotNull(project.Segments[0].GeneratedSpeech);
        Assert.Throws<ArgumentException>(()=>SegmentTools.Split(project,"one",TimeSpan.FromSeconds(5),"a","b","",""));
    }
    [Fact] public void MergeUsesChronologicalNeighbourFirstSpeakerAndNewSpeech()
    {
        var project=Project(); project=project with { Segments=project.Segments.Reverse().ToArray() };
        var updated=SegmentTools.MergeNext(project,"one"); var s=Assert.Single(updated.Segments);
        Assert.Equal("one",s.Id);Assert.Equal("a",s.SpeakerId);Assert.Equal("Hello there See you",s.Source.Text);Assert.Equal("Hola amigo Hasta luego",s.Translation.Text);
        Assert.Null(s.GeneratedSpeech);Assert.Null(s.FittedSpeech);Assert.Equal(TimeSpan.FromSeconds(8),s.End);
        Assert.Throws<InvalidOperationException>(()=>SegmentTools.MergeNext(project,"two"));
    }
    [Fact] public void CreateSortsAndDeleteDetachesVoiceReferenceWithoutDeletingFileReference()
    {
        var p=Project(); p=p with { Speakers=[new(){Id="a",VoiceReferenceSegmentId="one",VoiceReference=new("ref.wav")}] };
        var created=SegmentTools.Create(p,TimeSpan.Zero,TimeSpan.FromSeconds(.8),"Hi","","a");
        Assert.Equal("Hi",created.Segments[0].Source.Text);Assert.Equal(EmotionMode.SourceAudio,created.Segments[0].Emotion.Mode);
        Assert.Contains(PipelineStage.Translation,created.Segments[0].DirtyStages);
        var deleted=SegmentTools.Delete(created,"one");Assert.Equal(2,deleted.Segments.Count);Assert.Null(deleted.Speakers[0].VoiceReferenceSegmentId);Assert.Equal("ref.wav",deleted.Speakers[0].VoiceReference!.Path);
        Assert.Throws<ArgumentException>(()=>SegmentTools.Create(p,TimeSpan.FromSeconds(19),TimeSpan.FromSeconds(21),"Hi","",null));
    }
    [Fact] public async Task RecentProjectsPersistTenUniqueEntriesMostRecentFirst()
    {
        string root=Path.Combine(Path.GetTempPath(),"opendub-recent-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var store=new RecentProjectStore(Path.Combine(root,"recent.json"));
            for(int i=0;i<12;i++) await store.RecordAsync(Path.Combine(root,"project-"+i),default);
            await store.RecordAsync(Path.Combine(root,"project-4"),default);
            var read=new RecentProjectStore(Path.Combine(root,"recent.json")).Read();Assert.Equal(10,read.Count);Assert.Equal(Path.Combine(root,"project-4"),read[0]);Assert.Equal(10,read.Distinct().Count());
            Assert.DoesNotContain(Path.Combine(root,"project-0"),read);
        }
        finally { Directory.Delete(root,true); }
    }
}
