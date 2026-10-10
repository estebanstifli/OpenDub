using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dubbing.Application.Projects;

namespace Dubbing.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExport))]
    private string? exportedMediaPath;
    public bool HasExport => ExportedMediaPath is not null;

    [RelayCommand]
    private void OpenExportedMedia() => OpenExportLocation(false);
    [RelayCommand]
    private void OpenExportFolder() => OpenExportLocation(true);

    private void OpenExportLocation(bool folder)
    {
        ExportedMediaPath = ExportedMedia.Find(session.DirectoryPath, session.Project.LastExportFile);
        if (ExportedMediaPath is null) { Status = L["Exported file is no longer available."]; return; }
        string path = folder ? Path.GetDirectoryName(ExportedMediaPath)! : ExportedMediaPath;
        try
        {
            var start = new ProcessStartInfo { UseShellExecute = true, FileName = path };
            if (!OperatingSystem.IsWindows())
            {
                start.FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                start.ArgumentList.Add(path);
            }
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { Status = L["Could not open export."] + " " + ex.Message; }
    }
}
