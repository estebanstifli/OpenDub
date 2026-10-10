using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dubbing.Infrastructure.Projects;

namespace Dubbing.App.Services;

public sealed partial class ProjectDialogs
{
    private readonly RecentProjectStore recentProjects = recent ?? new();
    public Task RecordRecentProjectAsync(string directory, CancellationToken ct) => recentProjects.RecordAsync(directory, ct);
    public async Task<string?> OpenProjectAsync()
    {
        var dialog = new Window { Title = text["Open recents"], Width = 760, Height = 540, MinWidth = 500, MinHeight = 300,
            Background = Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var items = new StackPanel { Spacing = 8 };
        foreach (string path in recentProjects.Read())
        {
            bool exists = File.Exists(Path.Combine(path, "project.json"));
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, IsEnabled = exists,
                Content = new StackPanel { Spacing = 4, Children = {
                    new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = path, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = text["Project not found"], IsVisible = !exists, Foreground = Brushes.Salmon } } } };
            button.Click += (_, _) => dialog.Close(path); items.Children.Add(button);
        }
        if (items.Children.Count == 0) items.Children.Add(new TextBlock { Text = text["No recent projects yet."] });
        var browse = new Button { Content = text["Open project…"] }; var cancel = new Button { Content = text["Cancel"] };
        browse.Click += (_, _) => dialog.Close("browse"); cancel.Click += (_, _) => dialog.Close();
        var grid = new Grid { Margin = new(20), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 14 };
        grid.Children.Add(new TextBlock { Text = text["Open recents"], FontSize = 22, FontWeight = FontWeight.SemiBold });
        var list = new ScrollViewer { Content = items }; Grid.SetRow(list, 1); grid.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { browse, cancel } }; Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        dialog.Content = grid;
        var result = await ShowDialogAsync<string?>(dialog);
        return result == "browse" ? await BrowseProjectAsync() : result;
    }
}
