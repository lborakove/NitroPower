using Avalonia;
using Avalonia.Media;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui;

/// <summary>Names, colors and icons shared by every page.</summary>
public static class Visuals
{
    public static readonly Color Eco = Color.Parse("#34D399");
    public static readonly Color Quiet = Color.Parse("#60A5FA");
    public static readonly Color Balanced = Color.Parse("#A78BFA");
    public static readonly Color Performance = Color.Parse("#F59E0B");
    public static readonly Color Turbo = Color.Parse("#F43F5E");
    public static readonly Color Accent = Color.Parse("#2DD4BF");
    public static readonly Color Cpu = Color.Parse("#38BDF8");
    public static readonly Color Gpu = Color.Parse("#A3E635");

    public static Color ModeColor(string? id) => id switch
    {
        "eco" => Eco,
        "quiet" => Quiet,
        "balanced" => Balanced,
        "performance" => Performance,
        "turbo" => Turbo,
        _ => Accent,
    };

    public static IBrush ModeBrush(string? id) => new SolidColorBrush(ModeColor(id));

    /// <summary>A translucent fill of the mode color, for selected cards.</summary>
    public static IBrush ModeSoftBrush(string? id) => new SolidColorBrush(ModeColor(id), 0.14);

    public static string ModeName(string? id) => id is null ? "—" : Loc.T($"mode.{id}") is var t && t != $"mode.{id}" ? t : id;

    public static string ModeDescription(string id) => Loc.T($"mode.{id}.desc");

    public static string ModeIconKey(string? id) => id switch
    {
        "eco" => "ico.leaf",
        "quiet" => "ico.moon",
        "balanced" => "ico.scale",
        "performance" => "ico.bolt",
        "turbo" => "ico.flame",
        _ => "ico.gauge",
    };

    public static Geometry? Icon(string key) =>
        Application.Current is { } app && app.TryGetResource(key, null, out var value) ? value as Geometry : null;

    public static string Temp(double? c) => c is { } v ? $"{v:0} °C" : "—";

    public static string Percent(double? p) => p is { } v ? $"{v:0}%" : "—";

    public static string Rpm(int? rpm) => rpm is { } v ? string.Format(Loc.Instance.Culture, "{0:N0} RPM", v) : "—";

    public static string MHz(int? mhz) => mhz is { } v ? string.Format(Loc.Instance.Culture, "{0:N0} MHz", v) : "—";

    public static string GHz(int? mhz) => mhz is { } v ? string.Format(Loc.Instance.Culture, "{0:0.0} GHz", v / 1000.0) : "—";

    /// <summary>"a, b e c" / "a, b and c".</summary>
    public static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1
            ? string.Concat(list)
            : string.Join(", ", list.Take(list.Count - 1)) + $" {Loc.T("common.and")} " + list[^1];
    }

    /// <summary>Formats with the UI language's culture (decimal comma in Portuguese).</summary>
    public static string N(double value, string format) => value.ToString(format, Loc.Instance.Culture);

    public static string Offset(int v) => v > 0 ? $"+{v}" : v.ToString(Loc.Instance.Culture);

    /// <summary>"auto", "max", "60,40" or "curve" in words.</summary>
    public static string FanChoice(string? choice) => choice switch
    {
        null => "—",
        "auto" => Loc.T("fans.auto"),
        "max" => Loc.T("fans.max"),
        "curve" => Loc.T("fans.curve"),
        _ when choice.Split(',') is [var c, var g] => Loc.F("fans.manualValue", c, g),
        _ => choice,
    };
}
