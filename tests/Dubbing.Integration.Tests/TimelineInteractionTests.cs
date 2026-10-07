using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Dubbing.App.Controls;

namespace Dubbing.Integration.Tests;

public sealed class TimelineInteractionTests
{
    [AvaloniaFact] public void CursorCanBeDraggedAndKeyboardSeekPreservesTimeAcrossZoom()
    {
        var timeline = new TimelineControl { Width = 1118, Height = 208, Duration = 10 };
        timeline.SeekRequested += value => timeline.Playhead = value;
        var window = new Window { Width = 1118, Height = 208, Content = timeline };
        try
        {
            window.Show(); window.UpdateLayout();
            window.MouseDown(new Point(610, 60), MouseButton.Left);
            Assert.True(timeline.IsDragging); Assert.Equal(5, timeline.Playhead, 2);
            window.MouseMove(new Point(810, 60), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(810, 60), MouseButton.Left);
            Assert.False(timeline.IsDragging); Assert.Equal(7, timeline.Playhead, 2);
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Assert.Equal(6.9, timeline.Playhead, 2);
            Assert.Equal(0, timeline.PositionToSeconds(-100)); Assert.Equal(10, timeline.PositionToSeconds(9999));
            timeline.Width = 2118; window.Width = 2118; window.UpdateLayout();
            Assert.Equal(6.9, timeline.Playhead, 2); Assert.Equal(5, timeline.PositionToSeconds(1110), 2);
        }
        finally { window.Close(); }
    }
}
