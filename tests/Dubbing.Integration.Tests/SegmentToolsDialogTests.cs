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
