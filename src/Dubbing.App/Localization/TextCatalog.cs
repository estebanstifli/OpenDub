using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace Dubbing.App.Localization;

public sealed class TextCatalog : INotifyPropertyChanged
{
    private readonly string directory;
    private Dictionary<string, string> english = [];
    private Dictionary<string, string> translated = [];
    private readonly Dictionary<string, LocalizedValue> bindings = [];
    public LocalizedValue For(string key)
    {
        if (!bindings.TryGetValue(key, out var value)) bindings[key] = value = new(this[key]);
        return value;
    }
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public TextCatalog(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Languages");
        Load("en");
    }
    public string this[string key] => translated.GetValueOrDefault(key) ?? english.GetValueOrDefault(key) ?? key;
    public string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, this[key], values);
    public IReadOnlyList<string> AvailableLanguages => Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToArray() : ["en"];
    public void Load(string language)
    {
        if (language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid UI language.");
        english = Read("en"); translated = language == "en" ? [] : Read(language); Language = language;
        foreach (var pair in bindings) pair.Value.Update(this[pair.Key]);
        PropertyChanged?.Invoke(this, new("Item")); PropertyChanged?.Invoke(this, new("Item[]"));
    }
    private Dictionary<string, string> Read(string language)
    {
        string file = Path.Combine(directory, language + ".json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : [];
    }
}

public sealed class LocalizedValue(string value) : INotifyPropertyChanged
{
    public string Value { get; private set; } = value;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(string value) { Value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(LocalizedValue.Value))
    { Source = (Avalonia.Application.Current?.Resources["Localizer"] as TextCatalog ?? new TextCatalog()).For(key) };
}

public sealed class LocalizedValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (Avalonia.Application.Current?.Resources["Localizer"] as TextCatalog ?? new TextCatalog()).For(value?.ToString() ?? "");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
