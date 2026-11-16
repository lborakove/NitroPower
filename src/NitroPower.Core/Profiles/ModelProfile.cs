namespace NitroPower.Core.Profiles;

/// <summary>Per-model data file (profiles/*.json). Unknown JSON fields are ignored.</summary>
public sealed record ModelProfile(
    int SchemaVersion,
    string Id,
    string DisplayName,
    ProfileMatch Match,
    ThermalModes ThermalModes,
    GpuOverclock? GpuOverclock,
    FanInfo? Fans,
    IReadOnlyDictionary<string, string>? Features,
    NitroKeyInfo? NitroKey,
    ModeCycleConfig? ModeCycle = null,
    TurboConfig? Turbo = null)
{
    /// <summary>Profile schema versions this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    public ThermalMode? FindModeByPlatformProfile(string? platformProfile) =>
        platformProfile is null
            ? null
            : ThermalModes.Modes.FirstOrDefault(m => m.PlatformProfile == platformProfile);

    public ThermalMode? FindMode(string id) =>
        ThermalModes.Modes.FirstOrDefault(m => m.Id == id);

    /// <summary>The mode key cycle; when the profile has none: every mode but
    /// low-power on AC, and the modes allowed on battery.</summary>
    public ModeCycleConfig EffectiveModeCycle => ModeCycle ?? new ModeCycleConfig(
        ThermalModes.Modes.Where(m => m.PlatformProfile != "low-power").Select(m => m.Id).ToList(),
        ThermalModes.Modes.Where(m => !m.RequiresAc).Select(m => m.Id).ToList(),
        ThermalModes.Modes.Where(m => m.Id is "quiet" or "eco").Select(m => m.Id).ToList());

    /// <summary>Turbo; when the profile has none: the "turbo" mode, else the
    /// last listed one, with the fans at maximum.</summary>
    public TurboConfig EffectiveTurbo => Turbo ?? new TurboConfig(
        (FindMode("turbo") ?? ThermalModes.Modes[^1]).Id,
        FanSetting.MaxText);

    /// <summary>sysfs attribute (relative to the nitro_sense directory) for a
    /// feature, or null when the profile marks it absent or "unknown".</summary>
    public string? FeatureAttribute(string feature)
    {
        if (Features is null || !Features.TryGetValue(feature, out var value))
            return null;
        if (value is "unknown" or "none" or "")
            return null;
        var slash = value.LastIndexOf('/');
        return slash >= 0 ? value[(slash + 1)..] : value;
    }
}

public sealed record ProfileMatch(IReadOnlyDictionary<string, string> Dmi);

public sealed record ThermalModes(string Source, IReadOnlyList<ThermalMode> Modes, bool TurboSupported);

/// <summary>An operating mode as the firmware sees it.</summary>
/// <param name="Id">Stable id used by the API and UI: eco, quiet, balanced, performance, turbo.</param>
/// <param name="FirmwareValue">Value of WMI gaming misc setting 0x0B.</param>
/// <param name="PlatformProfile">Name in /sys/firmware/acpi/platform_profile.</param>
/// <param name="GpuOcLevel">Index into <see cref="GpuOcDevice.Levels"/>.</param>
/// <param name="RequiresAc">Firmware refuses the mode on battery.</param>
public sealed record ThermalMode(string Id, int FirmwareValue, string PlatformProfile, int GpuOcLevel, bool RequiresAc);

public sealed record GpuOverclock(GpuOcPolicyConfig Policy, IReadOnlyList<GpuOcDevice> Devices)
{
    public GpuOcDevice? FindDevice(string pciId, string subsystem) =>
        Devices.FirstOrDefault(d =>
            string.Equals(d.PciId, pciId, StringComparison.OrdinalIgnoreCase) &&
            (d.Subsystem is null or "*" || string.Equals(d.Subsystem, subsystem, StringComparison.OrdinalIgnoreCase)));
}

public sealed record GpuOcPolicyConfig(bool AcOnly, int BatteryResumePercent, int BatteryStopPercent);

/// <param name="PciId">"vendor:device", lowercase hex, e.g. "10de:28a0".</param>
/// <param name="Subsystem">"subvendor:subdevice", or "*" / null for any board.</param>
public sealed record GpuOcDevice(
    string PciId,
    string? Subsystem,
    string Name,
    IReadOnlyList<GpuClockOffsets> Levels,
    bool Validated);

/// <summary>GPU clock offsets in MHz on the NvAPI scale used by NitroSense.
/// NVML counts memory in transfer rate, so it receives twice <see cref="Mem"/>.</summary>
public sealed record GpuClockOffsets(int Core, int Mem)
{
    public static GpuClockOffsets None { get; } = new(0, 0);
    public int MemNvml => Mem * 2;
    public bool IsZero => Core == 0 && Mem == 0;
}

public sealed record FanInfo(string Control, int Count, string? CustomCurve);

/// <summary>What the Nitro/mode key steps through.</summary>
/// <param name="Ac">Mode ids in order, on AC.</param>
/// <param name="Battery">Mode ids in order, on battery.</param>
/// <param name="FansAutoOn">Modes that put the fans back on automatic when the
/// key reaches them (linuwu_sense does this for quiet and eco).</param>
public sealed record ModeCycleConfig(
    IReadOnlyList<string> Ac,
    IReadOnlyList<string> Battery,
    IReadOnlyList<string>? FansAutoOn)
{
    public bool ResetsFans(string modeId) => FansAutoOn?.Contains(modeId) == true;
}

/// <summary>What turbo does on this model, as in the linuwu_sense turbo toggle:
/// the top mode (which brings its GPU overclock level) and the fans.</summary>
/// <param name="Mode">Thermal mode id.</param>
/// <param name="Fans">"max", "auto", "keep" or "cpu,gpu" percent.</param>
public sealed record TurboConfig(string Mode, string Fans)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public FanSetting FanSetting => FanSetting.Parse(Fans);
}

/// <summary>A fan request: fixed speeds, the software curve, or keep what is set.</summary>
public sealed record FanSetting(Hardware.FanSpeeds? Speeds, bool IsCurve = false)
{
    public const string MaxText = "max";
    public const string AutoText = "auto";
    public const string KeepText = "keep";
    public const string CurveText = "curve";

    public static FanSetting Keep { get; } = new((Hardware.FanSpeeds?)null);
    public static FanSetting Curve { get; } = new(null, IsCurve: true);

    public bool IsKeep => Speeds is null && !IsCurve;

    /// <summary>"auto", "max", "cpu,gpu" or "curve" (null for keep).</summary>
    public string? ToText() => IsCurve ? CurveText : Speeds switch
    {
        null => null,
        { IsAuto: true } => AutoText,
        { Cpu: 100, Gpu: 100 } => MaxText,
        var s => s.Value.ToString(),
    };

    public static FanSetting Of(Hardware.FanSpeeds speeds) => new(speeds);

    public static FanSetting Parse(string? text) => TryParse(text) ??
        throw new FormatException($"fan setting '{text}' is not auto, max, curve, keep or cpu,gpu (0..100)");

    public static FanSetting? TryParse(string? text) => text?.Trim() switch
    {
        MaxText => new FanSetting(Hardware.FanSpeeds.Max),
        AutoText => new FanSetting(Hardware.FanSpeeds.Auto),
        KeepText => Keep,
        CurveText => Curve,
        var t => Hardware.FanSpeeds.TryParse(t) is { } s ? new FanSetting(s) : null,
    };
}

/// <param name="Devices">Input device names that carry the key; when absent,
/// <see cref="DefaultDevices"/>. External keyboards may also advertise the same code.</param>
public sealed record NitroKeyInfo(int EvdevCode, string Name, IReadOnlyList<string>? Devices = null)
{
    public static readonly IReadOnlyList<string> DefaultDevices = ["AT Translated Set 2 keyboard", "Acer WMI hotkeys"];

    public IReadOnlyList<string> EffectiveDevices => Devices is { Count: > 0 } d ? d : DefaultDevices;
}
