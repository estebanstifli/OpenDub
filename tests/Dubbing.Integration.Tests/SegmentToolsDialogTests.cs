using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dubbing.App.Controls;
using Dubbing.App.Services;

namespace Dubbing.Integration.Tests;

public class SegmentToolsDialogTests
{
    [AvaloniaFact]
    public async Task CreateShowsOccupiedSlotsAndBlocksOverlapAfterDragOrNumericEdits()
    {
        var owner=new Window();owner.Show();
        try
        {
            var occupied=new Dubbing.Core.Projects.DubSegment[] {
                new(){Start=TimeSpan.FromSeconds(2),End=TimeSpan.FromSeconds(4)},
                new(){Start=TimeSpan.FromSeconds(8),End=TimeSpan.FromSeconds(10),ExcludedFromDubbing=true}};
            var options=new SegmentRangeEditorOptions(false,12,Enumerable.Range(0,500).Select(i=>(float)(.12+Math.Abs(Math.Sin(i*.07))*.65)).ToArray(),
                new(4,8,6,"New dialogue","","","",null),[],(a,b,ct)=>Task.FromResult("text"),(a,b,ct)=>Task.CompletedTask,()=>{},occupied);
            var pending=new ProjectDialogs(owner).EditSegmentRangeAsync(options,default);var dialog=owner.OwnedWindows.Single();dialog.UpdateLayout();
            var wave=dialog.GetVisualDescendants().OfType<ReferenceWaveform>().Single();
            var create=dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Tag,"Create segment"));
            var times=dialog.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
            Assert.Equal(2,wave.ExistingRegions.Count);Assert.Equal("#2",wave.ExistingRegions[1].Label);Assert.True(create.IsEnabled);Assert.False(wave.HasConflict);
            // A numeric edit into the preceding segment is blocked without pressing Create.
            times[0].Value=3;Assert.False(create.IsEnabled);Assert.True(wave.HasConflict);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsVisible && t.Text=="Overlaps segments: 1. Move or resize the selection into an empty gap.");
            create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Assert.False(pending.IsCompleted); // Guard against programmatic invocation too.
            times[0].Value=4;Assert.True(create.IsEnabled);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="The new segment overlaps an existing segment. Choose an empty gap.");
            // Move the entire selection over the excluded segment: it still occupies a slot.
            var middle=wave.TranslatePoint(new Point(wave.Bounds.Width*.5,70),dialog)!.Value;
            var moved=middle.WithX(middle.X+wave.Bounds.Width/12);
            dialog.MouseDown(middle,MouseButton.Left);dialog.MouseMove(moved,RawInputModifiers.LeftMouseButton);dialog.MouseUp(moved,MouseButton.Left);
            Assert.True(wave.HasConflict);Assert.False(create.IsEnabled);
            dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Tag,"Zoom to selection")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2,wave.ExistingRegions.Count);Assert.True(wave.HasConflict);
            dialog.UpdateLayout();
            if(Environment.GetEnvironmentVariable("DUBBING_CREATE_OVERLAP_SCREENSHOT") is { } output)
            { using var frame=dialog.CaptureRenderedFrame();frame!.Save(output); }
            times[0].Value=4;times[1].Value=8;Assert.False(wave.HasConflict);Assert.True(create.IsEnabled);
            create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));var result=await pending;
            Assert.NotNull(result);Assert.Equal(4,result.Start);Assert.Equal(8,result.End);
        }
        finally {owner.Close();}
    }

    [AvaloniaFact]
    public async Task RecentPickerReturnsSelectedProjectAndShowsMissingEntriesDisabled()
    {
        string root=Path.Combine(Path.GetTempPath(),"recent-picker-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var owner=new Window();owner.Show();
        try
        {
            string found=Path.Combine(root,"found.dubproj"),missing=Path.Combine(root,"missing.dubproj");Directory.CreateDirectory(found);File.WriteAllText(Path.Combine(found,"project.json"),"{}");
            var store=new Dubbing.Infrastructure.Projects.RecentProjectStore(Path.Combine(root,"recents.json"));
            await store.RecordAsync(missing,default);await store.RecordAsync(found,default);
            var pending=new ProjectDialogs(owner,recent:store).OpenProjectAsync();var dialog=owner.OwnedWindows.Single();dialog.UpdateLayout();
            var buttons=dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Contains(buttons,b=>Equals(b.Content,"Open project…"));
            var unavailable=buttons.Single(b=>b.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==missing));Assert.False(unavailable.IsEnabled);
            buttons.Single(b=>b.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==found)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(found,await pending);
        }
        finally {owner.Close();Directory.Delete(root,true);}
    }

    [AvaloniaFact]
    public async Task SplitDialogTranscribesExactPartsAndCommitsOnlyOnAccept()
    {
        var owner=new Window();owner.Show(); var calls=new List<(double,double)>();
        try
        {
            var options=new SegmentRangeEditorOptions(true,20,Enumerable.Repeat(.5f,200).ToArray(),new(2,8,5,"Hello","there","Hola","amigo",null),[],
                (a,b,ct)=>{calls.Add((a,b));return Task.FromResult(a==2?"First text":"Second text");},(a,b,ct)=>Task.CompletedTask,()=>{});
            var pending=new ProjectDialogs(owner).EditSegmentRangeAsync(options,default);
            var dialog=owner.OwnedWindows.Single();dialog.UpdateLayout();
            void Click(string tag)=>dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Tag,tag)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var wave=dialog.GetVisualDescendants().OfType<ReferenceWaveform>().Single();
            var position=wave.TranslatePoint(new Point(wave.Bounds.Width*.6,50),dialog)!.Value;
            dialog.MouseDown(position,MouseButton.Left);dialog.MouseUp(position,MouseButton.Left);
            Assert.InRange(wave.SplitPoint!.Value,5.9,6.1);
            Click("Transcribe both parts");Assert.Equal(2,calls.Count);Assert.Equal(2,calls[0].Item1);Assert.Equal(calls[0].Item2,calls[1].Item1);Assert.Equal(8,calls[1].Item2);
            dialog.UpdateLayout();
            if(Environment.GetEnvironmentVariable("DUBBING_SEGMENT_TOOLS_SCREENSHOT") is { } output)
            { using var frame=dialog.CaptureRenderedFrame();frame!.Save(output); }
            Assert.False(pending.IsCompleted);Click("Split segment");var result=await pending;
            Assert.NotNull(result);Assert.Equal("First text",result.First);Assert.Equal("Second text",result.Second);Assert.Equal(calls[0].Item2,result.Split);
            pending=new ProjectDialogs(owner).EditSegmentRangeAsync(options,default);dialog=owner.OwnedWindows.Single();dialog.Close();Assert.Null(await pending);
        }
        finally {owner.Close();}
    }
    [AvaloniaFact]
    public async Task CreateRectangleCanMoveAndResizeAndCancelTranscription()
    {
        var owner=new Window();owner.Show();bool cancelled=false;
        try
        {
            var options=new SegmentRangeEditorOptions(false,20,Enumerable.Repeat(.5f,200).ToArray(),new(2,8,5,"Hello","","","",null),[],
                async(a,b,ct)=>{try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){cancelled=true;throw;}return "";},(a,b,ct)=>Task.CompletedTask,()=>{});
            var pending=new ProjectDialogs(owner).EditSegmentRangeAsync(options,default);var dialog=owner.OwnedWindows.Single();dialog.UpdateLayout();
            var wave=dialog.GetVisualDescendants().OfType<ReferenceWaveform>().Single();
            var middle=wave.TranslatePoint(new Point(wave.Bounds.Width*.25,40),dialog)!.Value;
            dialog.MouseDown(middle,MouseButton.Left);dialog.MouseMove(middle.WithX(middle.X+wave.Bounds.Width*.1),RawInputModifiers.LeftMouseButton);dialog.MouseUp(middle.WithX(middle.X+wave.Bounds.Width*.1),MouseButton.Left);
            Assert.InRange(wave.Start,3.99,4.01);Assert.InRange(wave.End,9.99,10.01);
            var edge=wave.TranslatePoint(new Point(wave.Bounds.Width*.5,40),dialog)!.Value;
            dialog.MouseDown(edge,MouseButton.Left);dialog.MouseMove(edge.WithX(edge.X+wave.Bounds.Width*.1),RawInputModifiers.LeftMouseButton);dialog.MouseUp(edge.WithX(edge.X+wave.Bounds.Width*.1),MouseButton.Left);
            Assert.InRange(wave.End,11.99,12.01);
            dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Tag,"Transcribe selection")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            dialog.Close();Assert.Null(await pending);Assert.True(cancelled);
        }
        finally {owner.Close();}
    }
}
