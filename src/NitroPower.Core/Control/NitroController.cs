using NitroPower.Core.Hardware;
using NitroPower.Core.IO;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Control;

/// <summary>A request this model or driver cannot satisfy. The message is for the user.</summary>
public sealed class NitroException(string message) : Exception(message);

public enum GpuApplyStatus
{
    /// <summary>No NVIDIA GPU, or no overclock entry for it in the profile.</summary>
    NotApplicable,

    /// <summary>The GPU is runtime-suspended; it was not woken.</summary>
    Suspended,

    Unchanged,
    Applied,
    Failed,
}

public sealed record GpuApplyResult(GpuApplyStatus Status, GpuClockOffsets? Offsets = null, string? Detail = null)
{
    public static GpuApplyResult NotApplicable { get; } = new(GpuApplyStatus.NotApplicable);
}

/// <summary>Outcome of a mode or turbo change.</summary>
/// <param name="From">Mode before (null if unknown).</param>
/// <param name="To">Mode after.</param>
/// <param name="Fans">Fan choice set as part of the change ("auto", "max", "cpu,gpu", "curve"), if any.</param>
public sealed record ModeChange(string? From, string To, string? Fans, GpuApplyResult Gpu)
{
    /// <summary>Turbo state afterwards, for turbo changes.</summary>
    public bool? Turbo { get; init; }

    /// <summary>False when nothing had to be done.</summary>
    public bool Changed { get; init; } = true;
}

/// <summary>API names of the on/off features and their nitro_sense attributes.</summary>
public static class NitroFeatures
{
    public const string BatteryLimiter = "batteryLimiter";
    public const string BatteryCalibration = "batteryCalibration";
    public const string KeyboardTimeout = "keyboardTimeout";
    public const string LcdOverdrive = "lcdOverdrive";
    public const string BootSound = "bootSound";

    public static readonly IReadOnlyDictionary<string, string> Attributes = new Dictionary<string, string>
    {
        [BatteryLimiter] = NitroSenseDevice.BatteryLimiter,
        [BatteryCalibration] = NitroSenseDevice.BatteryCalibration,
        [KeyboardTimeout] = NitroSenseDevice.KeyboardTimeout,
        [LcdOverdrive] = NitroSenseDevice.LcdOverdrive,
        [BootSound] = NitroSenseDevice.BootSound,
    };

    public static string Attribute(string id) =>
        Attributes.TryGetValue(id, out var attribute)
            ? attribute
            : throw new NitroException($"unknown feature '{id}'; features: {string.Join(", ", Attributes.Keys)}");
}

/// <summary>
/// Every NitroPower function, executed on the hardware: thermal modes, the
/// mode key, turbo, fans, battery, USB charging, keyboard, display, boot sound
/// and GPU overclock. The CLI uses it directly (as root); the daemon and the
/// GUI go through the same class. Writes need root.
/// </summary>
public sealed class NitroController
{
    private readonly ISysfs _fs;
    private readonly IGpuControl? _gpu;
    private GpuOverclockPolicy? _gpuPolicy;

    public NitroController(ISysfs fs, MachineInfo machine, StateStore store, IGpuControl? gpu)
    {
        _fs = fs;
        _gpu = gpu;
        Machine = machine;
        Store = store;
        Platform = new PlatformProfile(fs);
        Keyboard = FourZoneKeyboard.Detect(fs);
        FirmwareTurbo = TurboSense.Detect(fs);
        Module = new LinuwuModule(fs);
    }

    public MachineInfo Machine { get; }
    public StateStore Store { get; }
    public ModelProfile? Profile => Machine.Profile;
    public NitroSenseDevice? Sense => Machine.NitroSense;
    public PlatformProfile Platform { get; }
    public FourZoneKeyboard? Keyboard { get; }
    public TurboSense? FirmwareTurbo { get; }
    public LinuwuModule Module { get; }

    public NitroState State => Store.Load();
    public PowerState Power => PowerSupplies.Read(_fs);
    public ThermalMode? CurrentMode => Profile?.FindModeByPlatformProfile(Platform.Current);

    /// <summary>Turbo is on while NitroPower started it and the turbo mode is still selected.</summary>
    public bool IsTurboActive =>
        Profile is { } p && State.Turbo is not null && CurrentMode?.Id == p.EffectiveTurbo.Mode;

    /// <summary>Selects a thermal mode. Choosing another mode while turbo is on
    /// ends turbo and puts the fans back as they were.</summary>
    public ModeChange SetMode(string modeId)
    {
        var profile = RequireProfile();
        var mode = RequireMode(profile, modeId);
        var from = CurrentMode?.Id;
        var turboMode = profile.EffectiveTurbo.Mode;
        var state = Store.Load();

        var next = mode.Id == turboMode ? state : state with { LastNonTurboMode = mode.Id };
        string? fans = null;
        if (state.Turbo is { } snapshot && mode.Id != turboMode)
        {
            fans = snapshot.PreviousFans;
            next = next with { Turbo = null, Fans = fans };
        }

        Commit(state, next, () =>
        {
            Platform.Set(mode.PlatformProfile);
            if (fans is not null)
                ApplyFanChoice(fans);
        });
        return new ModeChange(from, mode.Id, fans, ApplyGpuPolicy());
    }

    /// <summary>A press of the Nitro/mode key: next mode in the cycle, or turbo
    /// on/off. Quiet and eco also put the fans on automatic.</summary>
    public ModeChange PressModeKey(ModeKeyBehavior behavior)
    {
        var profile = RequireProfile();
        var onAc = Power.OnAc;
        if (onAc && behavior == ModeKeyBehavior.TurboToggle)
            return ToggleTurbo();

        var step = ModeCycler.Next(profile, CurrentMode?.Id, onAc, behavior, Store.Load().LastNonTurboMode);
        var change = SetMode(step.ModeId);
        if (step.FansToAuto && change.Fans is null)
        {
            SetFans(FanSpeeds.Auto);
            change = change with { Fans = FanSetting.AutoText };
        }
        return change;
    }

    /// <summary>
    /// Turbo, as the linuwu_sense turbo toggle does it: the top mode (which
    /// brings its GPU overclock level) and the fans from the profile. Turning
    /// it off restores the mode and fans that were in place before.
    /// </summary>
    public ModeChange SetTurbo(bool on)
    {
        var profile = RequireProfile();
        var turbo = profile.EffectiveTurbo;
        var state = Store.Load();
        var current = CurrentMode?.Id;
        var active = state.Turbo is not null && current == turbo.Mode;

        if (on)
        {
            if (active)
                return new ModeChange(current, turbo.Mode, null, GpuApplyResult.NotApplicable) { Turbo = true, Changed = false };
            var mode = RequireMode(profile, turbo.Mode, "turbo");
            var fanSetting = turbo.FanSetting;
            var sense = fanSetting.Speeds is null ? Sense : RequireSense();
            // A stale snapshot (the mode was changed elsewhere while turbo was
            // on) still holds the fans from before turbo.
            var previousFans = state.Turbo?.PreviousFans ?? state.Fans ??
                               FanSetting.Of(sense?.GetFanSpeeds() ?? FanSpeeds.Auto).ToText()!;
            var snapshot = new TurboSnapshot(
                current ?? ModeCycler.FallbackFromTurbo(profile, state.LastNonTurboMode), previousFans);
            var fans = fanSetting.ToText();
            Commit(state, state with { Turbo = snapshot, Fans = fans ?? state.Fans }, () =>
            {
                Platform.Set(mode.PlatformProfile);
                if (fanSetting.Speeds is { } f)
                    sense!.SetFanSpeeds(f);
            });
            return new ModeChange(current, mode.Id, fans, ApplyGpuPolicy()) { Turbo = true };
        }

        if (state.Turbo is not { } snap)
            return new ModeChange(current, current ?? "?", null, GpuApplyResult.NotApplicable) { Turbo = false, Changed = false };

        // Restore the mode only if turbo's mode is still selected: the user (or
        // the driver, on unplugging AC) may already have moved on.
        ThermalMode? back = null;
        if (current == turbo.Mode)
        {
            back = profile.FindMode(snap.PreviousMode) is { } previous && (!previous.RequiresAc || Power.OnAc)
                ? previous
                : RequireMode(profile, ModeCycler.FallbackFromTurbo(profile, state.LastNonTurboMode));
        }
        var restoreFans = snap.PreviousFans;
        Commit(state, state with { Turbo = null, Fans = restoreFans }, () =>
        {
            if (back is not null)
                Platform.Set(back.PlatformProfile);
            if (Sense is not null)
                ApplyFanChoice(restoreFans);
        });
        return new ModeChange(current, back?.Id ?? current ?? "?", restoreFans, ApplyGpuPolicy()) { Turbo = false };
    }

    public ModeChange ToggleTurbo() => SetTurbo(!IsTurboActive);

    /// <summary>Fixed fan speeds (0,0 = automatic); stops the software curve.</summary>
    public void SetFans(FanSpeeds speeds)
    {
        RequireSense().SetFanSpeeds(speeds);
        SaveFanChoice(FanSetting.Of(speeds).ToText()!);
    }

    /// <summary>An explicit fan choice also becomes what turbo returns to.</summary>
    private void SaveFanChoice(string choice)
    {
        var state = Store.Load();
        var next = state with
        {
            Fans = choice,
            Turbo = state.Turbo is { } t ? t with { PreviousFans = choice } : null,
        };
        if (next != state)
            Store.Save(next);
    }

    /// <summary>"auto", "max", "cpu,gpu" or "curve".</summary>
    public void SetFanChoice(string choice)
    {
        var setting = FanSetting.TryParse(choice) ?? throw new NitroException(
            $"'{choice}' is not auto, max, curve or cpu,gpu (0..100)");
        if (setting.IsCurve)
            EnableFanCurve();
        else if (setting.Speeds is { } speeds)
            SetFans(speeds);
    }

    /// <summary>Hands the fans to the software curve; the daemon runs it.</summary>
    public void EnableFanCurve()
    {
        RequireSense();
        Store.Load().EffectiveFanCurve.Build();
        SaveFanChoice(FanSetting.CurveText);
    }

    /// <summary>Stores new curves (validated) and, by default, switches to them.</summary>
    public void SetFanCurve(FanCurveSettings curves, bool activate = true)
    {
        try
        {
            curves.Build();
        }
        catch (ArgumentException e)
        {
            throw new NitroException($"unsafe fan curve: {e.Message}");
        }
        var state = Store.Load();
        Store.Save(state with { FanCurve = curves });
        if (activate)
            SaveFanChoice(FanSetting.CurveText);
    }

    /// <summary>Writes speeds without changing the fan choice (used by the curve engine).</summary>
    public void WriteFanSpeeds(FanSpeeds speeds) => RequireSense().SetFanSpeeds(speeds);

    /// <summary>The fan choice in effect: the stored one, else what the driver reports.</summary>
    public string? FanChoice => Store.Load().Fans ??
                                (Sense?.GetFanSpeeds() is { } s ? FanSetting.Of(s).ToText() : null);

    public NitroKeyAction NitroKeyAction => Store.Load().EffectiveNitroKeyAction;

    public void SetNitroKeyAction(NitroKeyAction action)
    {
        var state = Store.Load();
        Store.Save(state with { NitroKeyAction = NitroKeyActions.ToText(action) });
    }

    /// <summary>Runs the Nitro key action; null when the action is gui or none.</summary>
    public ModeChange? PressNitroKey() => NitroKeyAction switch
    {
        NitroKeyAction.Turbo => ToggleTurbo(),
        NitroKeyAction.Cycle => PressModeKey(ModeKeyBehavior.Cycle),
        _ => null,
    };

    /// <summary>A 0/1 attribute of nitro_sense: battery_limiter,
    /// battery_calibration, backlight_timeout, lcd_override, boot_animation_sound.</summary>
    public void SetFeature(string attribute, bool enabled) =>
        RequireAttribute(attribute).SetFlag(attribute, enabled);

    public bool? GetFeature(string attribute) => Sense?.GetFlag(attribute);

    public void SetUsbCharging(int batteryThreshold) =>
        RequireAttribute(NitroSenseDevice.UsbCharging).SetUsbCharging(batteryThreshold);

    public void SetKeyboardEffect(KeyboardEffectState effect) => RequireKeyboard().SetEffect(effect);

    public void SetKeyboardZones(PerZoneColors colors) => RequireKeyboard().SetZones(colors);

    /// <summary>Behavior of the firmware mode key (WMI event 7 models).</summary>
    public void SetDriverModeKey(ModeKeyBehavior behavior)
    {
        if (!Module.IsLoaded)
            throw new NitroException("linuwu_sense is not loaded");
        Module.SetModeKey(behavior);
    }

    /// <summary>Offsets the current mode and power state call for.</summary>
    public GpuClockOffsets DesiredGpuOffsets()
    {
        if (Profile is not { } profile)
            return GpuClockOffsets.None;
        // One policy per controller, so the battery hysteresis holds in the daemon
        _gpuPolicy ??= new GpuOverclockPolicy(profile, Machine.GpuOcEntry);
        var state = State;
        _gpuPolicy.Enabled = !state.GpuOverclockDisabled;
        _gpuPolicy.Device = EffectiveGpuEntry(state);
        return _gpuPolicy.Decide(Platform.Current, Power);
    }

    /// <summary>Limits for user GPU levels (NvAPI scale, MHz). Tighter than
    /// manual offsets because the daemon applies them on every start.</summary>
    public const int GpuLevelCoreMin = -300, GpuLevelCoreMax = 300, GpuLevelMemMin = -1000, GpuLevelMemMax = 1500;

    /// <summary>The GPU entry with the user's levels, if any.</summary>
    public GpuOcDevice? EffectiveGpuEntry(NitroState? state = null)
    {
        if (Machine.GpuOcEntry is not { } entry)
            return null;
        var levels = (state ?? State).GpuLevels;
        return levels is { } l && l.Count == entry.Levels.Count ? entry with { Levels = l } : entry;
    }

    /// <summary>Replaces the offsets of each overclock level (null: back to the
    /// profile's) and applies the result.</summary>
    public GpuApplyResult SetGpuLevels(IReadOnlyList<GpuClockOffsets>? levels)
    {
        if (Machine.GpuOcEntry is not { } entry)
            throw new NitroException("this GPU has no overclock levels in the model profile");
        if (levels is not null)
        {
            if (levels.Count != entry.Levels.Count)
                throw new NitroException($"expected {entry.Levels.Count} levels, got {levels.Count}");
            foreach (var l in levels)
            {
                if (l.Core is < GpuLevelCoreMin or > GpuLevelCoreMax || l.Mem is < GpuLevelMemMin or > GpuLevelMemMax)
                    throw new NitroException(
                        $"offsets out of range (core {GpuLevelCoreMin}..{GpuLevelCoreMax}, memory {GpuLevelMemMin}..{GpuLevelMemMax} MHz)");
            }
            if (levels.SequenceEqual(entry.Levels))
                levels = null;
        }
        var state = Store.Load();
        Store.Save(state with { GpuLevels = levels?.ToList() });
        return ApplyGpuPolicy();
    }

    /// <summary>True when the GPU is runtime-suspended (reading it would wake it).</summary>
    public bool GpuSuspended => _gpu is not null && !_gpu.IsAwake;

    /// <summary>Offsets in effect; null when there is no GPU or it is suspended.</summary>
    public GpuClockOffsets? ReadGpuOffsets()
    {
        if (_gpu is null || Machine.NvidiaGpu is null || !_gpu.IsAwake)
            return null;
        try
        {
            return _gpu.GetOffsets();
        }
        catch (NvmlException e)
        {
            throw new NitroException(e.Message);
        }
    }

    /// <summary>Applies <see cref="DesiredGpuOffsets"/> if the GPU is awake.
    /// Never wakes the GPU and never throws for GPU errors.</summary>
    public GpuApplyResult ApplyGpuPolicy()
    {
        if (_gpu is null || Machine.GpuOcEntry is null)
            return GpuApplyResult.NotApplicable;
        try
        {
            if (!_gpu.IsAwake)
                return new GpuApplyResult(GpuApplyStatus.Suspended);
            var want = DesiredGpuOffsets();
            if (_gpu.GetOffsets() == want)
                return new GpuApplyResult(GpuApplyStatus.Unchanged, want);
            _gpu.SetOffsets(want);
            return new GpuApplyResult(GpuApplyStatus.Applied, want);
        }
        catch (NvmlException e)
        {
            return new GpuApplyResult(GpuApplyStatus.Failed, Detail: e.Message);
        }
    }

    public GpuApplyResult SetGpuOverclockEnabled(bool enabled)
    {
        var state = Store.Load();
        Store.Save(state with { GpuOverclockDisabled = !enabled });
        return ApplyGpuPolicy();
    }

    /// <summary>Manual offsets (NvAPI scale). Wakes the GPU. The next mode
    /// change applies the policy again.</summary>
    public void SetGpuOffsets(GpuClockOffsets offsets)
    {
        if (_gpu is null || Machine.NvidiaGpu is null)
            throw new NitroException("no NVIDIA GPU");
        if (offsets.Core is < -1000 or > 1000 || offsets.Mem is < -1000 or > 3000)
            throw new NitroException("offsets out of range (core -1000..1000, memory -1000..3000 MHz)");
        try
        {
            _gpu.SetOffsets(offsets);
        }
        catch (NvmlException e)
        {
            throw new NitroException(e.Message);
        }
    }

    /// <summary>Puts a stored fan choice back: speeds are written, a curve is left to the engine.</summary>
    private void ApplyFanChoice(string choice)
    {
        var setting = FanSetting.TryParse(choice) ?? FanSetting.Of(FanSpeeds.Auto);
        if (setting.Speeds is { } speeds)
            RequireSense().SetFanSpeeds(speeds);
    }

    private ModelProfile RequireProfile() =>
        Profile ?? throw new NitroException("this model has no NitroPower profile");

    private NitroSenseDevice RequireSense() =>
        Sense ?? throw new NitroException("linuwu_sense (nitro_sense/predator_sense) is not loaded");

    private NitroSenseDevice RequireAttribute(string attribute)
    {
        var sense = RequireSense();
        if (!sense.Has(attribute))
            throw new NitroException($"{attribute} is not available on this model");
        return sense;
    }

    private FourZoneKeyboard RequireKeyboard() =>
        Keyboard ?? throw new NitroException(
            "no four-zone RGB keyboard (four_zoned_kb) on this model or driver");

    private ThermalMode RequireMode(ModelProfile profile, string modeId, string? purpose = null)
    {
        var mode = profile.FindMode(modeId) ?? throw new NitroException(
            $"unknown mode '{modeId}'; this model has: {string.Join(", ", profile.ThermalModes.Modes.Select(m => m.Id))}");
        if (!Platform.IsAvailable)
            throw new NitroException("platform_profile is not available (is linuwu_sense loaded?)");
        if (!Platform.Choices.Contains(mode.PlatformProfile, StringComparer.Ordinal))
            throw new NitroException($"the driver does not offer '{mode.PlatformProfile}' (mode {mode.Id})");
        if (mode.RequiresAc && !Power.OnAc)
            throw new NitroException($"{purpose ?? "mode " + mode.Id} needs AC power");
        return mode;
    }

    /// <summary>Saves the new state, then touches the hardware; restores the
    /// old state if the hardware refuses. Saving first means a watcher woken by
    /// the platform_profile change already sees the new state.</summary>
    private void Commit(NitroState before, NitroState after, Action apply)
    {
        var changed = after != before;
        if (changed)
            Store.Save(after);
        try
        {
            apply();
        }
        catch
        {
            if (changed)
            {
                try { Store.Save(before); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}
