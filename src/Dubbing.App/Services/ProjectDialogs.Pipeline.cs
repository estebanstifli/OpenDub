using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using Dubbing.Core.Engines;
using Dubbing.App.Controls;

namespace Dubbing.App.Services;

public sealed partial class ProjectDialogs
{
    public async Task RunPipelineAsync(PipelineDialogOptions options, Func<Task> start, Action cancel)
    {
        var dialog = new Window { Title = text[options.OperationTitle ?? (options.Translation ? "Translate" : "Generate speech")], Width = 800, Height = 680,
            MinWidth = 650, MinHeight = 480, Background = Brush.Parse("#0F1A2B"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Task? work = null;
        bool background = false, closed = false;
        var fields = new StackPanel { Spacing = 12 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 14 };
        progress.Bind(ProgressBar.ValueProperty, new Binding("Progress") { Source = owner.DataContext });
        var runningStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        runningStatus.Bind(TextBlock.TextProperty, new Binding("Status") { Source = owner.DataContext });
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
        log.Bind(TextBox.TextProperty, new Binding("ActivityText") { Source = owner.DataContext });
        log.TextChanged += (_, _) => log.CaretIndex = log.Text?.Length ?? 0;
        var processing = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 12, IsVisible = false };
        Grid.SetRow(log, 1); processing.Children.Add(runningStatus); processing.Children.Add(log);
        Button Button(string label, string icon) => new() { Background = Brush.Parse("#17263A"), CornerRadius = new Avalonia.CornerRadius(6), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new LucideIcon { Kind = icon }, new TextBlock { Text = text[label] } } }, Tag = label };
        var begin = Button("Start", "play"); var cancelButton = Button("Cancel", "x");
        var backgroundButton = Button("Continue in background", "panel-left"); backgroundButton.IsVisible = false;
        var selectionFields = new StackPanel { Spacing = 10, IsVisible = options.OperationTitle is null };
        fields.Children.Add(selectionFields);
        selectionFields.Children.Add(new TextBlock { Text = text.Format("Segments available: {0}", options.SegmentCount), FontSize = 20, FontWeight = FontWeight.SemiBold });
        selectionFields.Children.Add(new TextBlock { Text = text.Format("Existing results: {0}", options.ExistingCount) });
        var all = new RadioButton { Content = text["All"], GroupName = "SegmentScope", IsChecked = options.AllSegments };
        var subset = new RadioButton { Content = text["Segments"], GroupName = "SegmentScope", IsChecked = !options.AllSegments };
        var ranges = new TextBox { Watermark = "3-5, 9, 12-15", Text = options.SegmentRanges, IsEnabled = !options.AllSegments, MinWidth = 260 };
        all.IsCheckedChanged += (_, _) => { options.AllSegments = all.IsChecked == true; ranges.IsEnabled = !options.AllSegments; };
        selectionFields.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { all, subset, ranges } });
        var overwrite = new CheckBox { Content = text["Overwrite existing in selection"], IsChecked = options.OverwriteExisting };
        selectionFields.Children.Add(overwrite);
        ComboBox? engine = null, model = null;
        TextBox? modelPath = null, prompt = null;
        if (options.Translation)
        {
            fields.Children.Add(new TextBlock { Text = text["Target language"] });
            var language = new ComboBox { ItemsSource = options.Languages, SelectedItem = options.TargetLanguage, HorizontalAlignment = HorizontalAlignment.Stretch };
            language.SelectionChanged += (_, _) => { if (language.SelectedItem is string code) options.TargetLanguage = code; };
            fields.Children.Add(language);
            fields.Children.Add(new TextBlock { Text = text["Translation prompt"] });
            prompt = new TextBox { Text = options.TranslationPrompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, Tag = "TranslationPrompt" };
            prompt.TextChanged += (_, _) => options.TranslationPrompt = prompt.Text ?? "";
            fields.Children.Add(prompt);
            var resetPrompt = Button("Reset prompt", "refresh-cw");
            resetPrompt.Click += (_, _) => prompt.Text = TranslationPrompts.Default; fields.Children.Add(resetPrompt);
            fields.Children.Add(new TextBlock { Text = text["Saved with this project. Add dialect or style instructions, e.g. Spanish from Spain. Output language and JSON format are supplied automatically. Existing translations change only when overwrite is enabled."], TextWrapping = TextWrapping.Wrap });
            fields.Children.Add(new TextBlock { Text = text["Translation engine"] });
            engine = new ComboBox { ItemsSource = options.Engines, SelectedItem = options.Engine, HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = new FuncDataTemplate<EngineDescriptor>((item, _) => new TextBlock { Text = item?.DisplayName }) };
            fields.Children.Add(engine);
            var llama = new StackPanel { Spacing = 8 };
            llama.Children.Add(new TextBlock { Text = text["Translation backend"] });
            var backend = new ComboBox { ItemsSource = new[] { "auto", "cpu", "cuda", "metal" }, SelectedItem = options.Backend };
            backend.SelectionChanged += (_, _) => { if (backend.SelectedItem is string value) options.Backend = value; };
            llama.Children.Add(backend);
            llama.Children.Add(new TextBlock { Text = text["Translation GGUF (blank uses installed default)"] });
            modelPath = new TextBox { Text = options.ModelPath }; llama.Children.Add(modelPath);
            fields.Children.Add(llama);
            var ollama = new StackPanel { Spacing = 8 };
            ollama.Children.Add(new TextBlock { Text = text["Ollama model"] });
            model = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            if (options.OllamaModel is { } saved) { model.ItemsSource = new[] { saved }; model.SelectedItem = saved; }
            ollama.Children.Add(model);
            var reload = Button("Refresh Ollama models", "refresh-cw"); ollama.Children.Add(reload);
            fields.Children.Add(ollama);
            async Task Load()
            {
                if (options.LoadOllamaModels is null) return;
                reload.IsEnabled = false; begin.IsEnabled = false;
                try
                {
                    var names = await options.LoadOllamaModels();
                    if (closed) return;
                    model.ItemsSource = names; model.SelectedItem = names.Contains(options.OllamaModel ?? "") ? options.OllamaModel : names.FirstOrDefault();
                    status.Text = names.Count == 0 ? text["No local chat models found in Ollama."] : "";
                }
                catch (Exception ex) { if (!closed) status.Text = ex.Message; }
                finally { reload.IsEnabled = true; begin.IsEnabled = true; }
            }
            reload.Click += async (_, _) => await Load();
            engine.SelectionChanged += async (_, _) =>
            {
                options.Engine = engine.SelectedItem as EngineDescriptor;
                llama.IsVisible = options.Engine?.ProviderId == "llama.cpp";
                ollama.IsVisible = options.Engine?.ProviderId == "ollama";
                if (ollama.IsVisible) await Load();
            };
            llama.IsVisible = options.Engine?.ProviderId == "llama.cpp";
            ollama.IsVisible = options.Engine?.ProviderId == "ollama";
            dialog.Opened += async (_, _) => { if (ollama.IsVisible) await Load(); };
            fields.Children.Add(new TextBlock { Text = text["Runtime overrides apply to this operation only. Overwrite also replaces manually edited translations in the selection."], TextWrapping = TextWrapping.Wrap });
        }
        else if (options.OperationTitle is null)
        {
            var fit = new CheckBox { Content = text["Fit speech to segment duration"], IsChecked = options.FitDuration };
            fit.IsCheckedChanged += (_, _) => options.FitDuration = fit.IsChecked == true; fields.Children.Add(fit);
            fields.Children.Add(new TextBlock { Text = text["Generates translated segments using their voice and emotion settings. Enable overwrite to regenerate existing audio."], TextWrapping = TextWrapping.Wrap });
        }
        options.ConfigureFields?.Invoke(fields);
        fields.Children.Add(status);
        begin.Click += async (_, _) =>
        {
            options.SegmentRanges = ranges.Text ?? "";
            options.OverwriteExisting = overwrite.IsChecked == true;
            try { options.SelectedNumbers = options.ParseSelection(); }
            catch (FormatException ex) { status.Text = text[ex.Message]; return; }
            if (options.CountPending?.Invoke(options.SelectedNumbers, options.OverwriteExisting) == 0)
            { status.Text = text["No pending segments in this selection. Enable overwrite to replace existing results."]; return; }
            options.Engine = engine?.SelectedItem as EngineDescriptor ?? options.Engine;
            options.OllamaModel = model?.SelectedItem as string ?? options.OllamaModel;
            options.ModelPath = modelPath?.Text ?? options.ModelPath;
            options.TranslationPrompt = prompt?.Text ?? options.TranslationPrompt;
            if (options.Translation && (options.Engine is null || !options.Languages.Contains(options.TargetLanguage) ||
                (options.Engine.ProviderId == "ollama" && model?.SelectedItem is not string)))
            { status.Text = text["Choose a language, engine and available model before starting."]; return; }
            try { options.ValidateFields?.Invoke(); }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            { status.Text = text[ex.Message]; return; }
            fields.IsVisible = false; processing.IsVisible = true; begin.IsVisible = false; backgroundButton.IsVisible = true;
            try { work = Invoke(); await work; if (dialog.IsVisible) dialog.Close(); }
            catch (OperationCanceledException) { if (dialog.IsVisible) dialog.Close(); }
            catch (Exception ex)
            {
                runningStatus.ClearValue(TextBlock.TextProperty);
                runningStatus.Text = text.Format("Operation failed: {0}", ex.Message);
                runningStatus.Foreground = Brush.Parse("#FFB4AB");
                Dubbing.Infrastructure.Processes.ActivityJournal.Write(runningStatus.Text);
                backgroundButton.IsVisible = false;
                progress.Foreground = Brush.Parse("#D64545");
                cancelButton.Content = Button("Close", "x").Content; cancelButton.Tag = "Close";
                // Keep the error and log visible until the user closes the dialog.
            }
        };
        async Task Invoke() => await start();
        cancelButton.Click += (_, _) => { if (work is null || !work.IsCompleted) cancel(); dialog.Close(); };
        backgroundButton.Click += (_, _) => { background = true; dialog.Close(); };
        dialog.Closing += (_, _) => { if (!background && (work is null || !work.IsCompleted)) cancel(); };
        dialog.Closed += (_, _) => closed = true;
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new(20), RowSpacing = 14 };
        layout.Children.Add(progress);
        var body = new Grid(); Grid.SetRow(body, 1); layout.Children.Add(body);
        body.Children.Add(new ScrollViewer { Content = fields }); body.Children.Add(processing);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { begin, backgroundButton, cancelButton } };
        Grid.SetRow(buttons, 2); layout.Children.Add(buttons); dialog.Content = layout;
        await ShowDialogAsync<object?>(dialog);
        if (work is not null) await work;
    }
}
