using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dubbing.App.Controls;

namespace Dubbing.App.Services;

public sealed record SegmentRangeEdit(double Start, double End, double Split, string First, string Second, string FirstTranslation, string SecondTranslation, string? SpeakerId);
public sealed record SegmentRangeEditorOptions(bool IsSplit, double Duration, IReadOnlyList<float> Peaks, SegmentRangeEdit Initial,
    IReadOnlyList<Dubbing.Core.Projects.SpeakerProfile> Speakers,
    Func<double, double, CancellationToken, Task<string>> Transcribe, Func<double, double, CancellationToken, Task> Preview, Action Stop, IReadOnlyList<Dubbing.Core.Projects.DubSegment>? ExistingSegments = null);

public sealed partial class ProjectDialogs
{
    public async Task<SegmentRangeEdit?> EditSegmentRangeAsync(SegmentRangeEditorOptions options, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? running = null;
        var dialog = new Window { Title = text[options.IsSplit ? "Split segment" : "Create segment"], Width = 980, Height = 780,
            MinWidth = 760, MinHeight = 580, Background = Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var edit = options.Initial;
        var existing = options.IsSplit ? Array.Empty<Dubbing.Core.Projects.DubSegment>() : options.ExistingSegments ?? [];
        bool busy = false;
        var wave = new ReferenceWaveform { Height = 160, Peaks = options.Peaks, WindowStart = 0, WindowEnd = options.Duration,
            Start = edit.Start, End = edit.End, SplitPoint = options.IsSplit ? edit.Split : null, AllowMove = !options.IsSplit };
        wave.ExistingRegions = existing.Select((s,i) => new WaveformRegion(s.Start.TotalSeconds, s.End.TotalSeconds, "#" + (i+1))).ToArray();
        var panel = new StackPanel { Spacing = 12 };
        TextBlock Label(string key) => new() { Text = text[key], TextWrapping = TextWrapping.Wrap };
        Button Button(string label, string icon) => new() { Tag = label, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new LucideIcon { Kind = icon }, Label(label) } } };
        panel.Children.Add(Label(options.IsSplit ? "Drag the gold line to split. The suggested text split is approximate; adjust both texts or transcribe both parts. New speech must be generated."
            : "Drag the rectangle to move it, or drag its white edges to resize. Enter text or transcribe the selected audio."));
        panel.Children.Add(wave);
        var viewLabel = new TextBlock(); panel.Children.Add(viewLabel);
        void ShowRange(double a, double b)
        {
            wave.WindowStart = a; wave.WindowEnd = b;
            int first = (int)(a/options.Duration*options.Peaks.Count), last = Math.Min(options.Peaks.Count, (int)Math.Ceiling(b/options.Duration*options.Peaks.Count));
            wave.Peaks = options.Peaks.Skip(first).Take(Math.Max(1,last-first)).ToArray(); wave.InvalidateVisual();
            viewLabel.Text = $@"{TimeSpan.FromSeconds(a):hh\:mm\:ss\.fff} — {TimeSpan.FromSeconds(b):hh\:mm\:ss\.fff}";
        }
        var zoom = Button("Zoom to selection", "zoom-in"); var full = Button("Show all", "maximize");
        zoom.Click += (_, _) => ShowRange(Math.Max(0, wave.Start-2), Math.Min(options.Duration,wave.End+2));
        full.Click += (_, _) => ShowRange(0,options.Duration);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { zoom, full } });
        NumericUpDown Number(double value) => new() { Minimum = 0, Maximum = (decimal)options.Duration, Increment = .01m, FormatString = "0.000", Value = (decimal)value, Width = 160 };
        var start = Number(edit.Start); var end = Number(edit.End); var split = Number(edit.Split);
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var (label, field) in new[] { ("Start (seconds)",start), ("End (seconds)",end), ("Split at (seconds)",split) })
        {
            if (field == split && !options.IsSplit) continue;
            if (field != split && options.IsSplit) field.IsEnabled = false;
            times.Children.Add(new StackPanel { Spacing = 4, Children = { Label(label), field } });
        }
        panel.Children.Add(times);
        var speakers = new ComboBox { ItemsSource = options.Speakers, DisplayMemberBinding = new Avalonia.Data.Binding("DisplayName"),
            SelectedItem = options.Speakers.FirstOrDefault(s=>s.Id==edit.SpeakerId), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(Label("Speaker")); panel.Children.Add(speakers);
        var firstText = new TextBox { Text = edit.First, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100 };
        var secondText = new TextBox { Text = edit.Second, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100 };
        var firstTranslation = new TextBox { Text = edit.FirstTranslation, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 85 };
        var secondTranslation = new TextBox { Text = edit.SecondTranslation, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 85 };
        var textGrid = new Grid { ColumnDefinitions = new(options.IsSplit ? "*,*" : "*"), ColumnSpacing = 14 };
        var firstPanel = new StackPanel { Spacing = 8, Children = { Label(options.IsSplit ? "First part" : "Original"), firstText, Label("Translation (optional)"), firstTranslation } };
        textGrid.Children.Add(firstPanel);
        if (options.IsSplit)
        {
            var secondPanel = new StackPanel { Spacing = 8, Children = { Label("Second part"), secondText, Label("Translation (optional)"), secondTranslation } };
            Grid.SetColumn(secondPanel,1); textGrid.Children.Add(secondPanel);
        }
        panel.Children.Add(textGrid);
        panel.Children.Add(Label("Transcription replaces the original text in this editor only. Review the result before saving. Translations are edited manually or generated later."));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon };
        var overlapWarning = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon, IsVisible = false };
        if (!options.IsSplit)
            panel.Children.Insert(2, Label("Grey rectangles: existing segments. Blue: new selection. Red: overlap. Create only inside an empty gap."));
        var progress = new ProgressBar { IsIndeterminate = true, Height = 8, IsVisible = false };
        var accept = Button(options.IsSplit ? "Split segment" : "Create segment", "scissors"); var cancel = Button("Cancel", "x");
        accept.Background = Brush.Parse("#2F7DF6");
        var transcribe = Button(options.IsSplit ? "Transcribe both parts" : "Transcribe selection", "audio-lines");
        var play = Button(options.IsSplit ? "Play first part" : "Play selection", "play"); var playSecond = Button("Play second part", "play"); var stop = Button("Stop", "square");
        var actions = new WrapPanel { Children = { play, stop, transcribe } }; if (options.IsSplit) actions.Children.Insert(1,playSecond);
        panel.Children.Insert(5, actions);
        bool syncing = false;
        bool ValidateSelection()
        {
            var conflicts = Dubbing.Application.Projects.SegmentTools.FindCreateOverlaps(existing, TimeSpan.FromSeconds(wave.Start), TimeSpan.FromSeconds(wave.End));
            wave.HasConflict = conflicts.Count > 0;
            overlapWarning.Text = wave.HasConflict ? text.Format("Overlaps segments: {0}. Move or resize the selection into an empty gap.", string.Join(", ", conflicts)) : "";
            overlapWarning.IsVisible = wave.HasConflict;
            accept.IsEnabled = !busy && !wave.HasConflict;
            wave.InvalidateVisual();
            return !wave.HasConflict;
        }
        void ReadTimes()
        {
            if (syncing) return;
            wave.Start = (double)(start.Value ?? 0); wave.End = (double)(end.Value ?? 0);
            if (wave.End > wave.Start && wave.Start >= 0 && wave.End <= options.Duration && (wave.Start < wave.WindowStart || wave.End > wave.WindowEnd))
                ShowRange(Math.Max(0,wave.Start-2),Math.Min(options.Duration,wave.End+2));
            wave.SplitPoint = options.IsSplit ? (double)(split.Value ?? 0) : null; ValidateSelection();
        }
        void TimesEdited() { error.Text=""; ReadTimes(); }
        start.ValueChanged += (_,_) => TimesEdited(); end.ValueChanged += (_,_) => TimesEdited(); split.ValueChanged += (_,_) => TimesEdited();
        wave.RangeChanged += (a,b) => { syncing=true; start.Value=(decimal)a; end.Value=(decimal)b; syncing=false; error.Text=""; ValidateSelection(); };
        wave.SplitChanged += at => { syncing=true; split.Value=(decimal)at; syncing=false; };
        SegmentRangeEdit Read(bool allowOverlap = false)
        {
            ReadTimes();
            if (wave.Start < 0 || wave.End > options.Duration || wave.End-wave.Start < .1 ||
                options.IsSplit && (wave.SplitPoint-wave.Start < .1 || wave.End-wave.SplitPoint < .1))
                throw new ArgumentException("Choose a range inside the video, at least 0.1 seconds long.");
            if (!ValidateSelection() && !allowOverlap) throw new ArgumentException("The new segment overlaps an existing segment. Choose an empty gap.");
            return new(wave.Start, wave.End, wave.SplitPoint ?? 0, firstText.Text ?? "", secondText.Text ?? "", firstTranslation.Text ?? "", secondTranslation.Text ?? "",
                (speakers.SelectedItem as Dubbing.Core.Projects.SpeakerProfile)?.Id);
        }
        async Task Run(Func<Task> action)
        {
            busy=true; panel.IsEnabled=false; accept.IsEnabled=false; progress.IsVisible=true; error.Text="";
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error.Text=text[ex.Message]; }
            finally { busy=false; panel.IsEnabled=true; progress.IsVisible=false; ValidateSelection(); }
        }
        transcribe.Click += async (_,_) =>
        {
            running = Run(async () => { var value=Read(); options.Stop();
                string a=await options.Transcribe(value.Start, options.IsSplit ? value.Split : value.End,cancellation.Token);
                string b=options.IsSplit ? await options.Transcribe(value.Split,value.End,cancellation.Token) : "";
                cancellation.Token.ThrowIfCancellationRequested(); firstText.Text=a; if(options.IsSplit) secondText.Text=b; }); await running;
        };
        play.Click += async (_,_) => { running=Run(async()=>{var v=Read(allowOverlap: true); await options.Preview(v.Start,options.IsSplit?v.Split:v.End,cancellation.Token);}); await running; };
        playSecond.Click += async (_,_) => { running=Run(async()=>{var v=Read(allowOverlap: true); await options.Preview(v.Split,v.End,cancellation.Token);}); await running; };
        stop.Click += (_,_) => options.Stop();
        accept.Click += (_,_) => { try { var result=Read(); if (string.IsNullOrWhiteSpace(result.First) || options.IsSplit && string.IsNullOrWhiteSpace(result.Second))
                    throw new ArgumentException("Enter or transcribe the original text for each part."); dialog.Close(result); }
            catch(Exception ex) when(ex is ArgumentException) { error.Text=text[ex.Message]; } };
        cancel.Click += (_,_) => dialog.Close(); dialog.Closing += (_,_) => { cancellation.Cancel(); options.Stop(); };
        var grid = new Grid { RowDefinitions = new("*,Auto,Auto,Auto"), RowSpacing=12, Margin=new(20) };
        grid.Children.Add(new ScrollViewer { Content=panel });
        var warnings = new StackPanel { Spacing=6, Children={overlapWarning,error} }; Grid.SetRow(warnings,1); grid.Children.Add(warnings); Grid.SetRow(progress,2); grid.Children.Add(progress);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal, Spacing=10, HorizontalAlignment=HorizontalAlignment.Right, Children={accept,cancel} };
        Grid.SetRow(buttons,3); grid.Children.Add(buttons); dialog.Content=grid;
        ShowRange(options.IsSplit ? Math.Max(0,edit.Start-2) : 0, options.IsSplit ? Math.Min(options.Duration,edit.End+2) : options.Duration);
        ValidateSelection();
        using var registration = ct.Register(()=>Avalonia.Threading.Dispatcher.UIThread.Post(()=>dialog.Close()));
        try { return await ShowDialogAsync<SegmentRangeEdit?>(dialog); }
        finally { cancellation.Cancel(); options.Stop(); if(running is not null) await running; }
    }
}
