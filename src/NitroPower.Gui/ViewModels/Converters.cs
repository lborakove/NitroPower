using Avalonia.Data.Converters;

namespace NitroPower.Gui.ViewModels;

/// <summary>String equality converters for segmented choices.</summary>
public static class Is
{
    public static readonly IValueConverter Auto = Equal("auto");
    public static readonly IValueConverter Max = Equal("max");
    public static readonly IValueConverter Curve = Equal("curve");
    public static readonly IValueConverter Manual = Equal("manual");

    private static FuncValueConverter<string?, bool> Equal(string value) => new(s => s == value);
}
