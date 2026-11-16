using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace NitroPower.Gui.Localization;

/// <summary>
/// UI text in Portuguese (Brazil) or English. XAML binds to the indexer through
/// <see cref="TrExtension"/>, so a language change updates the open window.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Auto = "auto", Portuguese = "pt", English = "en";

    public static Loc Instance { get; } = new();

    private Loc() => Language = FromEnvironment();

    /// <summary>"pt" or "en".</summary>
    public string Language { get; private set; }

    public CultureInfo Culture => Language == Portuguese ? CultureInfo.GetCultureInfo("pt-BR") : CultureInfo.GetCultureInfo("en-US");

    public string this[string key] => Strings.Get(key, Language);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>After <see cref="PropertyChanged"/>: view models rebuild their own text.</summary>
    public event Action? Changed;

    /// <param name="choice">"auto", "pt" or "en".</param>
    public void SetLanguage(string? choice)
    {
        var language = choice switch
        {
            Portuguese => Portuguese,
            English => English,
            _ => FromEnvironment(),
        };
        if (language == Language)
            return;
        Language = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        Changed?.Invoke();
    }

    /// <summary>LANGUAGE, LC_ALL, LC_MESSAGES, then LANG: Portuguese for "pt*", else English.</summary>
    public static string FromEnvironment()
    {
        foreach (var name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            return value.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? Portuguese : English;
        }
        return English;
    }

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args) =>
        string.Format(Instance.Culture, Instance[key], args);
}

/// <summary>{l:Tr key}: localized text that follows language changes.</summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
