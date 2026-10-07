using Dubbing.Core.Engines;

namespace Dubbing.App.Services;

public sealed class PipelineDialogOptions
{
    public string? OperationTitle { get; init; }
    public Action<Avalonia.Controls.StackPanel>? ConfigureFields { get; init; }
    public bool Translation { get; init; }
    public string TargetLanguage { get; set; } = "es";
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<EngineDescriptor> Engines { get; init; } = [];
    public EngineDescriptor? Engine { get; set; }
    public string Backend { get; set; } = "auto";
    public string ModelPath { get; set; } = "";
    public string? OllamaModel { get; set; }
    public Func<Task<IReadOnlyList<string>>>? LoadOllamaModels { get; init; }
    public bool FitDuration { get; set; } = true;
    public bool AllSegments { get; set; } = true;
    public string SegmentRanges { get; set; } = "";
    public bool OverwriteExisting { get; set; }
    public int ExistingCount { get; init; }
    public Func<IReadOnlySet<int>, bool, int>? CountPending { get; init; }
    public IReadOnlySet<int> SelectedNumbers { get; set; } = new HashSet<int>();

    public IReadOnlySet<int> ParseSelection()
    {
        if (AllSegments) return Enumerable.Range(1, SegmentCount).ToHashSet();
        var selected = new HashSet<int>();
        foreach (var part in SegmentRanges.Split(','))
        {
            var bounds = part.Trim().Split('-');
            if (bounds.Length is < 1 or > 2 || !int.TryParse(bounds[0].Trim(), out int first) || first < 1 || first > SegmentCount)
                throw new FormatException("Enter valid segment numbers or ranges, for example: 3,5,9 or 3-5,8.");
            int last = first;
            if (bounds.Length == 2 && (!int.TryParse(bounds[1].Trim(), out last) || last < first || last > SegmentCount))
                throw new FormatException("Enter valid segment numbers or ranges, for example: 3,5,9 or 3-5,8.");
            for (int i = first; i <= last; i++) selected.Add(i);
        }
        return selected;
    }
    public int SegmentCount { get; init; }
}
