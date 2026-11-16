using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Api;

public static class NitroApiProtocol
{
    public const string DefaultSocket = "/run/nitropower/nitropower.sock";

    /// <summary>Members of this group may use the daemon socket.</summary>
    public const string Group = "nitropower";

    /// <summary>ApiProtocolVersion from Directory.Build.props: bumped on incompatible changes.</summary>
    public static int Version { get; } = int.Parse(
        typeof(NitroApiProtocol).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ApiProtocolVersion")?.Value ?? "1",
        System.Globalization.CultureInfo.InvariantCulture);

    public static string SoftwareVersion { get; } =
        typeof(NitroApiProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "?";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <param name="Daemon">True when answered by nitropowerd, false for direct (local) access.</param>
public sealed record ApiInfo(string Version, int Protocol, bool Daemon);

/// <param name="GpuOcLevel">Index into <see cref="GpuStatus.Levels"/>.</param>
public sealed record ModeInfo(string Id, string PlatformProfile, bool RequiresAc, int GpuOcLevel = 0);

public sealed record KeyboardStatus(KeyboardEffectState? Effect, PerZoneColors? Zones);

/// <param name="Offsets">In effect; null when unknown (GPU suspended, not read).</param>
/// <param name="Wanted">What the mode and power state call for.</param>
/// <param name="Levels">Offsets per overclock level in use (the user's, else the profile's).</param>
/// <param name="ProfileLevels">The profile's offsets per level.</param>
/// <param name="Policy">When the overclock applies (AC only, battery thresholds).</param>
public sealed record GpuStatus(
    string PciId,
    string? OverclockName,
    bool OverclockEnabled,
    bool Suspended,
    GpuClockOffsets? Offsets,
    GpuClockOffsets Wanted,
    IReadOnlyList<GpuClockOffsets>? Levels = null,
    IReadOnlyList<GpuClockOffsets>? ProfileLevels = null,
    GpuOcPolicyConfig? Policy = null)
{
    /// <summary>The user replaced the profile's levels.</summary>
    public bool CustomLevels => Levels is not null && ProfileLevels is not null && !Levels.SequenceEqual(ProfileLevels);
}

/// <summary>Everything NitroPower knows, in one answer. Reading it costs several
/// firmware calls: fetch it on demand and after events, not in a fast loop
/// (use sensors for that).</summary>
public sealed record NitroStatus
{
    public required ApiInfo Api { get; init; }
    public string? Model { get; init; }
    public string? Bios { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public bool DriverLoaded { get; init; }
    public required PowerState Power { get; init; }

    public string? PlatformProfile { get; init; }
    public string? Mode { get; init; }
    public IReadOnlyList<ModeInfo> Modes { get; init; } = [];
    public bool Turbo { get; init; }
    public string? TurboReturnsTo { get; init; }
    public TurboConfig? TurboConfig { get; init; }

    /// <summary>What the mode key and "next mode" step through.</summary>
    public ModeCycleConfig? ModeCycle { get; init; }
    public bool? FirmwareTurboLed { get; init; }

    /// <summary>"auto", "max", "cpu,gpu" or "curve".</summary>
    public string? FanChoice { get; init; }

    /// <summary>Duty the driver reports (0 = automatic).</summary>
    public FanSpeeds? FanDuty { get; init; }

    public FanCurveSettings? FanCurve { get; init; }

    /// <summary>On/off features present on this model (null value = firmware did not say).</summary>
    public IReadOnlyDictionary<string, bool?> Features { get; init; } = new Dictionary<string, bool?>();

    public int? UsbCharging { get; init; }
    public KeyboardStatus? Keyboard { get; init; }
    public GpuStatus? Gpu { get; init; }
    public NitroKeyAction NitroKeyAction { get; init; }

    /// <summary>Firmware mode key behavior (WMI event 7 models).</summary>
    public ModeKeyBehavior? DriverModeKey { get; init; }

    public static NitroStatus Build(NitroController c, ApiInfo api, Func<GpuClockOffsets?> gpuOffsets)
    {
        var m = c.Machine;
        var p = c.Profile;
        var state = c.State;
        var mode = c.CurrentMode;
        var sense = c.Sense;
        var turbo = c.IsTurboActive;

        var features = new Dictionary<string, bool?>();
        if (sense is not null)
        {
            foreach (var (id, attribute) in NitroFeatures.Attributes)
            {
                if (sense.Has(attribute))
                    features[id] = sense.GetFlag(attribute);
            }
        }

        GpuStatus? gpu = null;
        if (m.NvidiaGpu is { } dev)
        {
            GpuClockOffsets? offsets = null;
            try { offsets = gpuOffsets(); }
            catch (NitroException) { }
            gpu = new GpuStatus(dev.PciId, m.GpuOcEntry?.Name, !state.GpuOverclockDisabled,
                c.GpuSuspended, offsets, c.DesiredGpuOffsets(),
                c.EffectiveGpuEntry(state)?.Levels, m.GpuOcEntry?.Levels, p?.GpuOverclock?.Policy);
        }

        return new NitroStatus
        {
            Api = api,
            Model = $"{m.Dmi["sys_vendor"]} {m.Dmi["product_name"]}".Trim(),
            Bios = m.Dmi["bios_version"],
            ProfileId = p?.Id,
            ProfileName = p?.DisplayName,
            DriverLoaded = sense is not null,
            Power = c.Power,
            PlatformProfile = c.Platform.Current,
            Mode = mode?.Id,
            Modes = p?.ThermalModes.Modes.Select(x => new ModeInfo(x.Id, x.PlatformProfile, x.RequiresAc, x.GpuOcLevel)).ToList() ?? [],
            Turbo = turbo,
            TurboReturnsTo = turbo ? state.Turbo!.PreviousMode : null,
            TurboConfig = p?.EffectiveTurbo,
            ModeCycle = p?.EffectiveModeCycle,
            FirmwareTurboLed = c.FirmwareTurbo?.IsOn,
            FanChoice = c.FanChoice,
            FanDuty = sense?.GetFanSpeeds(),
            FanCurve = state.EffectiveFanCurve,
            Features = features,
            UsbCharging = sense is not null && sense.Has(NitroSenseDevice.UsbCharging) ? sense.GetUsbCharging() : null,
            Keyboard = c.Keyboard is { } kb ? new KeyboardStatus(kb.GetEffect(), kb.GetZones()) : null,
            Gpu = gpu,
            NitroKeyAction = state.EffectiveNitroKeyAction,
            DriverModeKey = c.Module.ModeKey,
        };
    }
}

/// <summary>Something changed; clients refresh what they show.</summary>
/// <param name="Type">"mode", "power", "nitroKey" or "changed" (after an API call).</param>
public sealed record NitroEvent(string Type)
{
    public string? Mode { get; init; }
    public bool? Turbo { get; init; }
    public bool? OnAc { get; init; }
    public int? BatteryPercent { get; init; }
    public NitroKeyAction? Action { get; init; }

    /// <summary>API method that caused a "changed" event.</summary>
    public string? Method { get; init; }

    /// <summary>Why a Nitro key action could not run (e.g. turbo on battery).</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Every NitroPower operation. <see cref="LocalNitroApi"/> runs them in this
/// process (CLI as root, and the daemon itself); <see cref="NitroClient"/>
/// sends them to nitropowerd. The GUI and the CLI program against this.
/// </summary>
public interface INitroApi
{
    ApiInfo Info();
    NitroStatus Status();
    SensorSnapshot Sensors();

    ModeChange SetMode(string mode);

    /// <summary>Next mode in the cycle (what the mode key does).</summary>
    ModeChange NextMode();

    ModeChange SetTurbo(bool on);
    ModeChange ToggleTurbo();

    /// <summary>"auto", "max", "cpu,gpu" or "curve".</summary>
    void SetFans(string choice);

    void SetFanCurve(FanCurveSettings curves);

    /// <summary>See <see cref="NitroFeatures"/> for the ids.</summary>
    void SetFeature(string feature, bool enabled);

    void SetUsbCharging(int batteryThreshold);
    void SetKeyboardEffect(KeyboardEffectState effect);
    void SetKeyboardZones(PerZoneColors zones);

    GpuApplyResult SetGpuOverclock(bool enabled);
    GpuApplyResult ApplyGpuOverclock();
    /// <summary>Offsets for each overclock level, kept across restarts; null
    /// returns to the profile's. Applies at once.</summary>
    GpuApplyResult SetGpuLevels(IReadOnlyList<GpuClockOffsets>? levels);

    /// <summary>"turbo", "cycle", "gui" or "none".</summary>
    void SetNitroKeyAction(string action);

    /// <summary>Same as pressing the Nitro key.</summary>
    ModeChange? PressNitroKey();

    /// <summary>"cycle" or "turbo" (driver parameter, WMI key models).</summary>
    void SetModeKey(string behavior);

    IReadOnlyList<string> ListPresets();
    Preset? GetPreset(string name);
    void SavePreset(string name);
    IReadOnlyList<PresetResult> LoadPreset(string name);
    bool DeletePreset(string name);
}

/// <summary><see cref="INitroApi"/> executed in this process with a <see cref="NitroController"/>.</summary>
public sealed class LocalNitroApi(
    NitroController controller,
    PresetStore presets,
    SensorReader sensors,
    bool daemon = false,
    Func<GpuClockOffsets?>? gpuOffsets = null) : INitroApi
{
    public NitroController Controller => controller;

    public ApiInfo Info() => new(NitroApiProtocol.SoftwareVersion, NitroApiProtocol.Version, daemon);

    public NitroStatus Status() => NitroStatus.Build(controller, Info(), gpuOffsets ?? controller.ReadGpuOffsets);

    public SensorSnapshot Sensors() => sensors.Read();

    public ModeChange SetMode(string mode) =>
        mode == "turbo" && controller.Profile is { } p && p.FindMode("turbo") is null
            ? controller.SetTurbo(true)
            : controller.SetMode(mode);

    public ModeChange NextMode() => controller.PressModeKey(ModeKeyBehavior.Cycle);
    public ModeChange SetTurbo(bool on) => controller.SetTurbo(on);
    public ModeChange ToggleTurbo() => controller.ToggleTurbo();
    public void SetFans(string choice) => controller.SetFanChoice(choice);
    public void SetFanCurve(FanCurveSettings curves) => controller.SetFanCurve(curves);
    public void SetFeature(string feature, bool enabled) => controller.SetFeature(NitroFeatures.Attribute(feature), enabled);
    public void SetUsbCharging(int batteryThreshold) => controller.SetUsbCharging(batteryThreshold);
    public void SetKeyboardEffect(KeyboardEffectState effect) => controller.SetKeyboardEffect(effect);
    public void SetKeyboardZones(PerZoneColors zones) => controller.SetKeyboardZones(zones);
    public GpuApplyResult SetGpuOverclock(bool enabled) => controller.SetGpuOverclockEnabled(enabled);
    public GpuApplyResult ApplyGpuOverclock() => controller.ApplyGpuPolicy();
    public GpuApplyResult SetGpuLevels(IReadOnlyList<GpuClockOffsets>? levels) => controller.SetGpuLevels(levels);

    public void SetNitroKeyAction(string action) => controller.SetNitroKeyAction(
        NitroKeyActions.Parse(action) ?? throw new NitroException($"'{action}' is not turbo, cycle, gui or none"));

    public ModeChange? PressNitroKey() => controller.PressNitroKey();

    public void SetModeKey(string behavior) => controller.SetDriverModeKey(behavior switch
    {
        "cycle" => ModeKeyBehavior.Cycle,
        "turbo" => ModeKeyBehavior.TurboToggle,
        _ => throw new NitroException($"'{behavior}' is not cycle or turbo"),
    });

    public IReadOnlyList<string> ListPresets() => presets.List();
    public Preset? GetPreset(string name) => presets.Load(name);
    public void SavePreset(string name) => presets.Save(name, Presets.Capture(controller));

    public IReadOnlyList<PresetResult> LoadPreset(string name) =>
        Presets.Apply(controller, presets.Load(name) ?? throw new NitroException($"no preset '{name}'"));

    public bool DeletePreset(string name) => presets.Delete(name);
}
