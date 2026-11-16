using NitroPower.Core.Hardware;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Policy;

/// <summary>
/// Which GPU offsets the current state calls for, following NitroSense: the
/// firmware mode picks an OC level, which applies only on AC, and is dropped
/// below <see cref="GpuOcPolicyConfig.BatteryStopPercent"/> until the battery
/// is back at <see cref="GpuOcPolicyConfig.BatteryResumePercent"/>.
/// Stateful (battery hysteresis): keep one instance per daemon.
/// </summary>
public sealed class GpuOverclockPolicy(ModelProfile profile, GpuOcDevice? device)
{
    private bool _batteryOk = true;

    /// <summary>The GPU entry and its levels (the user may replace the levels).</summary>
    public GpuOcDevice? Device { get; set; } = device;

    /// <summary>When false, the user turned the overclock off: always zero.</summary>
    public bool Enabled { get; set; } = true;

    public GpuClockOffsets Decide(string? platformProfile, PowerState power)
    {
        var config = profile.GpuOverclock?.Policy;
        if (config is not null && power.BatteryPercent is { } pct)
        {
            if (pct < config.BatteryStopPercent)
                _batteryOk = false;
            else if (pct >= config.BatteryResumePercent)
                _batteryOk = true;
        }

        if (!Enabled || Device is not { } device)
            return GpuClockOffsets.None;
        if (config is { AcOnly: true } && !power.OnAc)
            return GpuClockOffsets.None;
        if (!_batteryOk)
            return GpuClockOffsets.None;

        var mode = profile.FindModeByPlatformProfile(platformProfile);
        if (mode is null || mode.GpuOcLevel < 0 || mode.GpuOcLevel >= device.Levels.Count)
            return GpuClockOffsets.None;
        return device.Levels[mode.GpuOcLevel];
    }
}
