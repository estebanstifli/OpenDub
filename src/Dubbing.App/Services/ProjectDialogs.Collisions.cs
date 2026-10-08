using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dubbing.Application.Projects;

namespace Dubbing.App.Services;

public sealed partial class ProjectDialogs
{
    public async Task<PlaybackOverlapChoice> ConfirmPlaybackOverlapsAsync()
    {
        var window = new Window { Title = text["Overlapping dialogue"], Width = 570, SizeToContent = SizeToContent.Height,
            CanResize = false, Background = Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 18, Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = text["Some segments overlap."], FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = text["You can play anyway or review the overlapping segments. Ignoring overlaps applies only to this playback."], TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, choice) in new[] { ("Ignore and play", PlaybackOverlapChoice.IgnoreAndPlay), ("View overlaps", PlaybackOverlapChoice.Review) })
        {
            var button = new Button { Content = text[label], Tag = label, Margin = new(8, 0, 0, 0) };
            button.Click += (_, _) => window.Close(choice);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); window.Content = panel;
        return await ShowDialogAsync<PlaybackOverlapChoice?>(window) ?? PlaybackOverlapChoice.Cancel;
    }

    public async Task<CollisionDecision> EditOverlapAsync(Dubbing.App.ViewModels.OverlapEditorModel model, CancellationToken ct)
    {
        var dialog = new OverlapEditorWindow(model, text, ct, owner.DataContext);
        try { return await ShowDialogAsync<CollisionDecision?>(dialog) ?? new(CollisionChoice.Cancel); }
        finally { await dialog.FinishAsync(); }
    }
    public async Task<CollisionDecision> ResolveCollisionAsync(SegmentCollision c)
    {
        var window = new Window { Title = text["Overlapping dialogue"], Width = 700, Height = 510, MinWidth = 570, MinHeight = 440,
            Background = Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        TextBlock Label(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 14, Margin = new(22) };
        panel.Children.Add(Label(text.Format("Segment {0} overlaps segment {1} by {2:F1} ms.", c.FirstNumber, c.SecondNumber, (c.AudibleEnd - c.Second.Start).TotalMilliseconds)));
        panel.Children.Add(Label($"{c.First.Start.TotalSeconds:F3}–{c.AudibleEnd.TotalSeconds:F3} s → {c.Second.Start.TotalSeconds:F3}–{c.Second.End.TotalSeconds:F3} s"));
        if (c.AudibleEnd > c.First.End)
            panel.Children.Add(Label(text.Format("Generated audio ends at {0:F3} s; the segment ends at {1:F3} s.", c.AudibleEnd.TotalSeconds, c.First.End.TotalSeconds)));
        panel.Children.Add(Label(c.First.Translation.Text)); panel.Children.Add(Label(c.Second.Translation.Text));
        panel.Children.Add(Label(text["Do not overlap trims excess audio at the boundary; this may cut speech. Merge joins both texts and regenerates speech using the first speaker. Keep overlap plays both voices together."]));
        var all = new CheckBox { Content = text["Apply to remaining overlaps (limit or keep only)"] }; panel.Children.Add(all);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, choice) in new[] { ("Do not overlap", CollisionChoice.Limit), ("Merge segments", CollisionChoice.Merge), ("Keep overlap", CollisionChoice.Allow), ("Cancel", CollisionChoice.Cancel) })
        {
            var button = new Button { Content = text[label], Margin = new(0, 0, 8, 8), IsEnabled = choice != CollisionChoice.Limit || c.Second.Start > c.First.Start, Tag = label };
            button.Click += (_, _) => window.Close(new CollisionDecision(choice, all.IsChecked == true && choice is CollisionChoice.Limit or CollisionChoice.Allow));
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); window.Content = new ScrollViewer { Content = panel };
        return await ShowDialogAsync<CollisionDecision?>(window) ?? new(CollisionChoice.Cancel);
    }
}
