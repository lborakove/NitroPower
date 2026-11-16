using System.Text.RegularExpressions;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.Services;

/// <summary>The daemon answers in English; known messages are shown in the UI language.</summary>
public static partial class Messages
{
    public static string Translate(string message)
    {
        if (Loc.Instance.Language == Loc.English)
            return message;
        var m = NeedsAc().Match(message);
        if (m.Success)
        {
            var what = m.Groups[1].Value;
            var mode = ModeOf().Match(what);
            return mode.Success
                ? Loc.F("err.modeNeedsAc", Visuals.ModeName(mode.Groups[1].Value))
                : what == "turbo" ? Loc.T("err.turboNeedsAc") : Loc.F("err.needsAc", what);
        }
        if (message.StartsWith("unsafe fan curve", StringComparison.Ordinal))
            return Loc.T("err.unsafeCurve") + message["unsafe fan curve".Length..];
        if (message.Contains("is not available on this model", StringComparison.Ordinal))
            return Loc.T("err.notAvailable");
        if (message.StartsWith("offsets out of range", StringComparison.Ordinal))
            return Loc.T("err.offsetsRange") + message["offsets out of range".Length..];
        if (message.StartsWith("the driver refused the change", StringComparison.Ordinal))
            return Loc.T("err.driverRefused");
        if (message.Contains("not loaded", StringComparison.Ordinal))
            return Loc.T("err.driverMissing");
        if (message.StartsWith("invalid preset name", StringComparison.Ordinal))
            return Loc.T("err.presetName");
        return message;
    }

    [GeneratedRegex("^(.+) needs AC power$")]
    private static partial Regex NeedsAc();

    [GeneratedRegex("^mode (\\w+)$")]
    private static partial Regex ModeOf();
}
