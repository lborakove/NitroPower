using NitroPower.Core.Hardware;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Policy;

/// <summary>A mode change requested by the mode key.</summary>
/// <param name="ModeId">Mode to switch to.</param>
/// <param name="FansToAuto">Put the fans back on automatic as well.</param>
public sealed record ModeStep(string ModeId, bool FansToAuto);

/// <summary>
/// What a press of the Nitro/mode key does, following linuwu_sense
/// (acer_thermal_profile_change): on battery it alternates between the
/// battery modes; on AC it steps through the AC modes, or toggles turbo; quiet
/// and eco put the fans back on automatic. Unlike the driver, the cycle keeps
/// its order when the top mode is not a firmware turbo.
/// </summary>
public static class ModeCycler
{
    public static ModeStep Next(
        ModelProfile profile,
        string? currentModeId,
        bool onAc,
        ModeKeyBehavior behavior,
        string? lastNonTurboModeId)
    {
        var cycle = profile.EffectiveModeCycle;
        string next;
        if (!onAc)
            next = After(cycle.Battery, currentModeId);
        else if (behavior == ModeKeyBehavior.Cycle)
            next = After(cycle.Ac, currentModeId);
        else
            next = TurboToggleTarget(profile, currentModeId, lastNonTurboModeId);
        return new ModeStep(next, cycle.ResetsFans(next));
    }

    /// <summary>Turbo when not in turbo, else back to the last other AC mode.</summary>
    public static string TurboToggleTarget(ModelProfile profile, string? currentModeId, string? lastNonTurboModeId)
    {
        var turbo = profile.EffectiveTurbo.Mode;
        return currentModeId == turbo ? FallbackFromTurbo(profile, lastNonTurboModeId) : turbo;
    }

    /// <summary>Where turbo returns to when nothing better is known: the
    /// remembered mode if it is still valid, else balanced, else the first AC mode.</summary>
    public static string FallbackFromTurbo(ModelProfile profile, string? remembered)
    {
        var turbo = profile.EffectiveTurbo.Mode;
        if (remembered is not null && remembered != turbo && profile.FindMode(remembered) is not null)
            return remembered;
        if (profile.FindMode("balanced") is not null && turbo != "balanced")
            return "balanced";
        return profile.EffectiveModeCycle.Ac.FirstOrDefault(m => m != turbo) ?? turbo;
    }

    private static string After(IReadOnlyList<string> list, string? current)
    {
        var i = current is null ? -1 : IndexOf(list, current);
        return i < 0 ? list[0] : list[(i + 1) % list.Count];
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }
        return -1;
    }
}
