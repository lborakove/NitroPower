using NitroPower.Core.IO;

namespace NitroPower.Core.Hardware;

/// <summary>What a press of the firmware mode key does in linuwu_sense
/// (parameter cycle_gaming_thermal_profile).</summary>
public enum ModeKeyBehavior
{
    /// <summary>Step through the modes.</summary>
    Cycle,

    /// <summary>Switch between turbo and the previous mode.</summary>
    TurboToggle,
}

/// <summary>Parameters of the loaded linuwu_sense module.</summary>
public sealed class LinuwuModule(ISysfs fs)
{
    public const string ParametersPath = "/sys/module/linuwu_sense/parameters";
    public const string CycleParameter = "cycle_gaming_thermal_profile";

    /// <summary>Parameters NitroPower reports; the last two come from driver patch 0002.</summary>
    public static readonly string[] KnownParameters =
    [
        CycleParameter, "predator_v4", "nitro_v4", "force_caps", "force_series",
        "experimental_ph315_predator_v4", "turbo_sense", "four_zone_kb",
    ];

    public bool IsLoaded => fs.Exists(ParametersPath);

    public string? Get(string name) => fs.ReadText($"{ParametersPath}/{name}");

    /// <summary>True when the driver carries patch 0002 (feature overrides).</summary>
    public bool HasFeatureOverrides => fs.Exists($"{ParametersPath}/four_zone_kb");

    /// <summary>Only applies to models whose mode key arrives as WMI event 7
    /// (most Predators). Writable at runtime.</summary>
    public ModeKeyBehavior? ModeKey => Get(CycleParameter) switch
    {
        "Y" or "1" => ModeKeyBehavior.Cycle,
        "N" or "0" => ModeKeyBehavior.TurboToggle,
        _ => null,
    };

    public void SetModeKey(ModeKeyBehavior behavior) =>
        fs.WriteText($"{ParametersPath}/{CycleParameter}", behavior == ModeKeyBehavior.Cycle ? "Y" : "N");
}

/// <summary>
/// turbo_sense/turbo_state: the firmware turbo LED on models with a turbo
/// key (PH315-52/53 and similar). The driver toggles it and calls
/// sysfs_notify, so the file can be polled.
/// </summary>
public sealed class TurboSense
{
    public const string StatePath = NitroSenseDevice.PlatformPath + "/turbo_sense/turbo_state";

    private readonly ISysfs _fs;

    private TurboSense(ISysfs fs) => _fs = fs;

    public static TurboSense? Detect(ISysfs fs) => fs.Exists(StatePath) ? new TurboSense(fs) : null;

    public bool? IsOn => _fs.ReadText(StatePath) switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };
}
