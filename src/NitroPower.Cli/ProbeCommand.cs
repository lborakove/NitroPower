using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.IO;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;
using static NitroPower.Cli.Output;

namespace NitroPower.Cli;

/// <summary>Read-only hardware report. Does not wake the GPU.</summary>
internal static class ProbeCommand
{
    public static int Run(CliSession session, Args args)
    {
        args.End();
        var c = session.Local.Controller;
        var catalog = session.Catalog;
        var fs = Sysfs.Real;
        var m = c.Machine;

        Section("Machine");
        Row("Model", $"{m.Dmi["sys_vendor"]} {m.Dmi["product_name"]}");
        Row("BIOS", $"{m.Dmi["bios_version"]} ({m.Dmi["bios_date"]})");
        Row("Profile", m.Profile is null ? "none (unsupported model)" : $"{m.Profile.Id} — {m.Profile.DisplayName}");
        foreach (var error in catalog.Errors)
            Row("Profile error", error);

        Section("Daemon");
        Row("nitropowerd", session.UsesDaemon
            ? $"running ({session.Api.Info().Version}), socket {session.SocketPath}"
            : session.DaemonFailure == NitroPower.Core.Api.NitroClient.ConnectFailure.PermissionDenied
                ? "running, but you are not in the nitropower group"
                : "not running");
        Row("Nitro key action", NitroKeyActions.ToText(c.NitroKeyAction));
        Row("Fan choice", Output.FanChoice(c.FanChoice));

        Section("Driver");
        Row("linuwu_sense", m.NitroSense is null ? "not loaded" : m.NitroSense.BasePath);
        Row("Feature overrides", c.Module.IsLoaded
            ? c.Module.HasFeatureOverrides
                ? $"yes (turbo_sense={c.Module.Get("turbo_sense")}, four_zone_kb={c.Module.Get("four_zone_kb")})"
                : "no (driver without NitroPower patch 0002)"
            : "-");
        Row("Mode key (WMI event 7)", c.Module.Get(LinuwuModule.CycleParameter) switch
        {
            "Y" => "cycle modes",
            "N" => "toggle turbo",
            _ => "unknown",
        });
        Row("platform_profile", c.Platform.IsAvailable
            ? $"{c.Platform.Current}  [{string.Join(" ", c.Platform.Choices)}]"
            : "unavailable");
        if (c.CurrentMode is { } mode)
            Row("Firmware mode", $"{mode.Id} (WMI value {mode.FirmwareValue})");

        if (m.Profile is { } p)
        {
            Section("Mode key and turbo");
            var cycle = p.EffectiveModeCycle;
            Row("Cycle on AC", string.Join(" → ", cycle.Ac));
            Row("Cycle on battery", string.Join(" → ", cycle.Battery));
            Row("Fans to auto on", cycle.FansAutoOn is { Count: > 0 } f ? string.Join(", ", f) : "-");
            Row("Turbo", $"mode {p.EffectiveTurbo.Mode}, fans {p.EffectiveTurbo.Fans}" +
                         (p.ThermalModes.TurboSupported ? " (firmware turbo)" : " (no firmware turbo)"));
            Row("Turbo state", c.IsTurboActive ? $"on, returns to {c.State.Turbo!.PreviousMode}" : "off");
            Row("Firmware turbo LED", c.FirmwareTurbo is { } ft ? Flag(ft.IsOn) : "not present");
        }

        if (m.NitroSense is { } ns)
        {
            Section("Features");
            Row("Fans", Fans(ns.GetFanSpeeds()));
            Row("Battery limit 80%", Flag(ns.GetFlag(NitroSenseDevice.BatteryLimiter)));
            Row("Battery calibration", Flag(ns.GetFlag(NitroSenseDevice.BatteryCalibration)));
            Row("USB power when off", Usb(ns.GetUsbCharging()));
            Row("Keyboard light timeout", Flag(ns.GetFlag(NitroSenseDevice.KeyboardTimeout)));
            Row("LCD overdrive", Flag(ns.GetFlag(NitroSenseDevice.LcdOverdrive)));
            Row("Boot sound", Flag(ns.GetFlag(NitroSenseDevice.BootSound)));
        }

        Section("RGB keyboard");
        if (c.Keyboard is { } kb)
        {
            Row("Effect", Effect(kb.GetEffect()));
            Row("Zones", Zones(kb.GetZones()));
        }
        else
        {
            Row("four_zoned_kb", "not present");
        }

        Section("GPU");
        Row("Device", m.NvidiaGpu is null ? "no NVIDIA GPU" : $"{m.NvidiaGpu.PciId} subsystem {m.NvidiaGpu.Subsystem} at {m.NvidiaGpu.Address}");
        Row("Overclock entry", m.GpuOcEntry is null
            ? "none for this GPU"
            : $"{m.GpuOcEntry.Name}: " + string.Join("  ", m.GpuOcEntry.Levels.Select((l, i) => $"L{i} +{l.Core}/+{l.Mem}")) +
              (m.GpuOcEntry.Validated ? "" : "  (not validated by Acer)"));
        Row("Overclock", c.State.GpuOverclockDisabled ? "disabled by the user" : "enabled");

        if (GpuRuntime.IsAwake(fs))
        {
            try
            {
                var off = c.ReadGpuOffsets();
                Row("Clock offsets", Offsets(off));
                if (m.Profile is not null)
                {
                    var want = c.DesiredGpuOffsets();
                    Row("Policy wants", Offsets(want) + (want == off ? "  ✓ applied" : "  ✗ differs"));
                }
            }
            catch (NitroException e)
            {
                Row("NVML", e.Message);
            }
        }
        else
        {
            Row("State", "runtime-suspended (not woken for the probe)");
        }

        Section("Sensors");
        var reader = new SensorReader(fs, () =>
        {
            using var nvml = NvmlSession.Open();
            return nvml.GetTelemetry();
        });
        reader.Read(); // first CPU usage sample
        Thread.Sleep(500);
        var s = reader.Read();
        Row("Power", $"{(s.Power.OnAc ? "AC" : "battery")}, battery {s.Power.BatteryPercent?.ToString() ?? "?"}%");
        Row("CPU", $"{Temp(s.CpuTempC)}, {s.CpuUsagePercent:0}% usage, {s.CpuFreqAvgMHz} MHz avg / {s.CpuFreqMaxMHz} MHz max");
        Row("GPU", $"{Temp(s.GpuTempC)}" + (s.Gpu is { } g
            ? $", {g.UtilizationPercent}% usage, {g.CoreClockMHz} / {g.MemClockMHz} MHz, {g.PowerW:0.0} W of {g.PowerLimitW:0} W"
            : ""));
        Row("System / SSD", $"{Temp(s.SystemTempC)} / {Temp(s.SsdTempC)}");
        Row("Fans", $"CPU {s.CpuFanRpm?.ToString() ?? "?"} RPM, GPU {s.GpuFanRpm?.ToString() ?? "?"} RPM");
        return 0;
    }
}
