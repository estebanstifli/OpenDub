using CommunityToolkit.Mvvm.ComponentModel;
using Dubbing.Core.Engines;

namespace Dubbing.App.ViewModels;

public partial class EngineRowViewModel : ObservableObject
{
    public EngineTask Task { get; }
    public string Label { get; }
    public IReadOnlyList<EngineDescriptor> Options { get; }
    [ObservableProperty] private EngineDescriptor? selected;
    public event Action<EngineRowViewModel>? Changed;
    public EngineRowViewModel(EngineTask task, string label, IReadOnlyList<EngineDescriptor> options, string? selectedId)
    {
        Task = task; Label = label; Options = options;
        selected = options.FirstOrDefault(x => x.EngineId == selectedId);
    }
    partial void OnSelectedChanged(EngineDescriptor? value) => Changed?.Invoke(this);
}
