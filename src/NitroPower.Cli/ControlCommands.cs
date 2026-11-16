using System.Globalization;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;
using static NitroPower.Cli.Output;

namespace NitroPower.Cli;

/// <summary>Commands that read or change one function each, through <see cref="INitroApi"/>.</summary>
internal static class ControlCommands
{
    private static readonly (string Id, string Label)[] FeatureLabels =
    [
        (NitroFeatures.BatteryLimiter, "Battery limit 80%"),
        (NitroFeatures.BatteryCalibration, "Battery calibration"),
        (NitroFeatures.KeyboardTimeout, "Keyboard light timeout"),
        (NitroFeatures.LcdOverdrive, "LCD overdrive"),
        (NitroFeatures.BootSound, "Boot sound"),
    ];

    public static string LabelOf(string featureId) => FeatureLabels.First(f => f.Id == featureId).Label;

    public static int Status(CliSession s, Args args)
    {
        args.End();
        var st = s.Api.Status();
        Row("Model", $"{st.Model} (BIOS {st.Bios})" + (st.ProfileId is null ? ", no profile" : ""));
        Row("Control", st.Api.Daemon ? $"nitropowerd {st.Api.Version}" : "direct (nitropowerd not running)");
        Row("Power", $"{(st.Power.OnAc ? "AC" : "battery")}, battery {st.Power.BatteryPercent?.ToString() ?? "?"}%");
        Row("Mode", st.Modes.Count > 0 ? ModeList(st.Modes, st.Mode) : st.PlatformProfile ?? "unknown");
        Row("Turbo", st.Turbo ? $"on (returns to {st.TurboReturnsTo})" : "off");
        if (st.FirmwareTurboLed is { } led)
            Row("Firmware turbo LED", Flag(led));
        if (!st.DriverLoaded)
            Row("linuwu_sense", "not loaded");
        Row("Fans", FanChoice(st.FanChoice) + (st.FanChoice == FanSetting.CurveText && st.FanDuty is { } d
            ? $" (now {Fans(d)})"
            : ""));
        foreach (var (id, label) in FeatureLabels)
        {
            if (st.Features.TryGetValue(id, out var v))
                Row(label, Flag(v));
        }
        if (st.UsbCharging is not null || st.DriverLoaded)
            Row("USB power when off", Usb(st.UsbCharging));
        Row("RGB keyboard", st.Keyboard is { } kb ? Effect(kb.Effect) : "not available");
        Row("GPU overclock", Gpu(st.Gpu));
        Row("Nitro key", NitroKeyActions.ToText(st.NitroKeyAction));
        if (st.DriverModeKey is { } mk)
            Row("Mode key (WMI)", mk == ModeKeyBehavior.Cycle ? "cycles modes" : "toggles turbo");
        return 0;
    }

    private static string Gpu(GpuStatus? g)
    {
        if (g is null)
            return "no NVIDIA GPU";
        if (g.OverclockName is null)
            return "no overclock entry for this GPU";
        var enabled = g.OverclockEnabled ? "enabled" : "disabled";
        if (g.Offsets is null)
            return $"{enabled}; mode wants {Offsets(g.Wanted)}" + (g.Suspended ? "; GPU suspended" : "");
        return $"{enabled}; {Offsets(g.Offsets)}" + (g.Offsets == g.Wanted ? "" : $" (mode wants {Offsets(g.Wanted)})");
    }

    public static int Mode(CliSession s, Args args)
    {
        var target = args.Next();
        args.End();
        if (target is null)
        {
            var st = s.Api.Status();
            if (st.Modes.Count == 0)
                throw new NitroException("this model has no NitroPower profile");
            Console.WriteLine(ModeList(st.Modes, st.Mode) + (st.Turbo ? "  (turbo on)" : ""));
            if (st.Mode is null)
                Console.WriteLine($"platform_profile is '{st.PlatformProfile}', which no mode of the profile uses");
            return 0;
        }
        s.RequireWrite("changing the mode");
        Change(target == "next" ? s.Api.NextMode() : s.Api.SetMode(target));
        return 0;
    }

    public static int Turbo(CliSession s, Args args)
    {
        var action = args.Next();
        args.End();
        if (action is null)
        {
            var st = s.Api.Status();
            Console.WriteLine(st.Turbo ? $"Turbo is on (returns to {st.TurboReturnsTo})." : "Turbo is off.");
            if (st.TurboConfig is { } t)
                Console.WriteLine($"Turbo = mode {t.Mode}, fans {t.Fans}" +
                                  (st.Gpu?.OverclockName is not null ? ", plus that mode's GPU overclock" : ""));
            return 0;
        }
        s.RequireWrite("turbo");
        Change(action.ToLowerInvariant() == "toggle" ? s.Api.ToggleTurbo() : s.Api.SetTurbo(Args.ParseOnOff(action)));
        return 0;
    }

    public static int Fan(CliSession s, Args args)
    {
        var first = args.Next();
        if (first is null)
        {
            args.End();
            var st = s.Api.Status();
            Console.WriteLine(FanChoice(st.FanChoice) + (st.FanDuty is { } d ? $" (driver: {Fans(d)})" : ""));
            return 0;
        }
        if (first == "curve")
            return FanCurveCommand(s, args);

        string choice;
        if (first is "auto" or "max")
            choice = first;
        else
        {
            var cpu = Args.ParseInt(first, "CPU fan", 0, 100);
            var gpu = Args.ParseInt(args.Require("GPU fan percent"), "GPU fan", 0, 100);
            choice = new FanSpeeds(cpu, gpu).ToString();
        }
        args.End();
        s.RequireWrite("setting the fans");
        s.Api.SetFans(choice);
        Console.WriteLine($"Fans {FanChoice(choice)}");
        return 0;
    }

    private static int FanCurveCommand(CliSession s, Args args)
    {
        var action = args.Next();
        var st = s.Api.Status();
        var curves = st.FanCurve ?? FanCurveSettings.Default;
        switch (action)
        {
            case null:
                args.End();
                Console.WriteLine(st.FanChoice == FanSetting.CurveText ? "Fan curve: active" : "Fan curve: not active");
                Row("CPU fan", Curve(curves.Cpu));
                Row("GPU fan", Curve(curves.Gpu));
                return 0;
            case "on":
                args.End();
                s.RequireWrite("the fan curve");
                s.Api.SetFans(FanSetting.CurveText);
                break;
            case "reset":
                args.End();
                s.RequireWrite("the fan curve");
                s.Api.SetFanCurve(FanCurveSettings.Default);
                curves = FanCurveSettings.Default;
                break;
            case "set":
                var which = args.Require("cpu, gpu or both");
                var points = ParseCurve(args.Require("points, e.g. 40:20,60:40,80:80,90:100"));
                args.End();
                curves = which switch
                {
                    "cpu" => curves with { Cpu = points },
                    "gpu" => curves with { Gpu = points },
                    "both" => new FanCurveSettings(points, points),
                    _ => throw new UsageException("fan curve set takes cpu, gpu or both"),
                };
                s.RequireWrite("the fan curve");
                s.Api.SetFanCurve(curves);
                break;
            default:
                throw new UsageException("fan curve takes on, set, reset (or nothing to show it)");
        }
        Console.WriteLine("Fan curve active:");
        Row("CPU fan", Curve(curves.Cpu));
        Row("GPU fan", Curve(curves.Gpu));
        if (!s.UsesDaemon)
            Console.WriteLine("Note: nitropowerd runs the curve; it is not running, so the fans stay as they are.");
        return 0;
    }

    private static List<FanCurvePoint> ParseCurve(string text)
    {
        var points = new List<FanCurvePoint>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split(':');
            if (pair.Length != 2 ||
                !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var temp) ||
                !int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duty))
                throw new UsageException($"'{part}' is not temperature:percent");
            points.Add(new FanCurvePoint(temp, duty));
        }
        try
        {
            _ = new FanCurve(points);
        }
        catch (ArgumentException e)
        {
            throw new UsageException($"unsafe fan curve: {e.Message}");
        }
        return points;
    }

    private static string Curve(IEnumerable<FanCurvePoint> points) =>
        string.Join("  ", points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.TempC:0}°C→{p.Percent}%")));

    /// <summary>The on/off attributes of nitro_sense.</summary>
    public static int Feature(CliSession s, Args args, string featureId)
    {
        var label = LabelOf(featureId);
        var value = args.Next();
        args.End();
        if (value is null)
        {
            var st = s.Api.Status();
            Console.WriteLine(st.Features.TryGetValue(featureId, out var v) ? $"{label}: {Flag(v)}" : $"{label}: not available");
            return 0;
        }
        var on = Args.ParseOnOff(value);
        s.RequireWrite(label);
        s.Api.SetFeature(featureId, on);
        Console.WriteLine($"{label}: {Flag(on)}");
        return 0;
    }

    public static int UsbCharging(CliSession s, Args args)
    {
        var value = args.Next();
        args.End();
        if (value is null)
        {
            Console.WriteLine($"USB power when off: {Usb(s.Api.Status().UsbCharging)}");
            return 0;
        }
        var threshold = value == "off" ? 0 : Args.ParseInt(value, "USB charging threshold", 0, 30);
        if (!NitroSenseDevice.UsbChargingLevels.Contains(threshold))
            throw new UsageException("USB charging must be off, 10, 20 or 30");
        s.RequireWrite("USB charging");
        s.Api.SetUsbCharging(threshold);
        Console.WriteLine($"USB power when off: {Usb(threshold)}");
        return 0;
    }

    public static int GpuOc(CliSession s, Args args)
    {
        var action = args.Next();
        if (action is null)
        {
            args.End();
            Console.WriteLine(Gpu(s.Api.Status().Gpu));
            return 0;
        }
        switch (action)
        {
            case "apply":
                args.End();
                s.RequireWrite("the GPU overclock");
                var applied = s.Api.ApplyGpuOverclock();
                if (applied.Status == GpuApplyStatus.NotApplicable)
                    Console.WriteLine("No GPU overclock for this model and GPU.");
                Output.Gpu(applied);
                return 0;
            case "on" or "off":
                args.End();
                s.RequireWrite("the GPU overclock");
                var result = s.Api.SetGpuOverclock(action == "on");
                Console.WriteLine($"GPU overclock {(action == "on" ? "enabled" : "disabled")}");
                Output.Gpu(result);
                return 0;
            case "levels":
                return GpuLevels(s, args);
            case "reset":
                args.End();
                ManualOffsets(s).SetGpuOffsets(GpuClockOffsets.None);
                Console.WriteLine("GPU offsets reset to 0 (the policy applies again on the next mode change)");
                return 0;
            default:
                var core = Args.ParseInt(action, "core offset", -1000, 1000);
                var mem = Args.ParseInt(args.Require("memory offset"), "memory offset", -1000, 3000);
                args.End();
                ManualOffsets(s).SetGpuOffsets(new GpuClockOffsets(core, mem));
                Console.WriteLine($"GPU offsets set: {Offsets(new GpuClockOffsets(core, mem))}");
                return 0;
        }
    }

    /// <summary>Manual offsets are a test tool for root without the daemon:
    /// nitropowerd puts the mode's offsets back within seconds, and its API
    /// only takes per-level offsets within safer limits.</summary>
    private static NitroController ManualOffsets(CliSession s)
    {
        if (s.UsesDaemon)
            throw new NitroException("nitropowerd manages the GPU offsets; set them per mode with 'gpu-oc levels', " +
                                     "or stop nitropowerd to test manual offsets");
        if (!Environment.IsPrivilegedProcess)
            throw new NitroException("manual GPU offsets need root: run it with sudo");
        return s.Local.Controller;
    }

    /// <summary>gpu-oc levels [reset | core/mem ...]: offsets per overclock level.</summary>
    private static int GpuLevels(CliSession s, Args args)
    {
        var first = args.Next();
        if (first is null)
        {
            var gpu = s.Api.Status().Gpu;
            if (gpu?.Levels is not { } levels)
            {
                Console.WriteLine("No GPU overclock levels for this model and GPU.");
                return 0;
            }
            for (var i = 0; i < levels.Count; i++)
            {
                var stock = gpu.ProfileLevels is { } p && i < p.Count && p[i] != levels[i] ? $"  (profile: {Offsets(p[i])})" : "";
                Console.WriteLine($"L{i}  {Offsets(levels[i])}{stock}");
            }
            return 0;
        }
        s.RequireWrite("the GPU overclock levels");
        GpuApplyResult result;
        if (first == "reset")
        {
            args.End();
            result = s.Api.SetGpuLevels(null);
            Console.WriteLine("GPU overclock levels back to the model profile's");
        }
        else
        {
            var parsed = new List<GpuClockOffsets>();
            for (var item = first; item is not null; item = args.Next())
            {
                var parts = item.Split('/');
                if (parts.Length != 2)
                    throw new NitroException($"'{item}' is not core/mem (e.g. 100/200)");
                parsed.Add(new GpuClockOffsets(
                    Args.ParseInt(parts[0], "core offset", NitroController.GpuLevelCoreMin, NitroController.GpuLevelCoreMax),
                    Args.ParseInt(parts[1], "memory offset", NitroController.GpuLevelMemMin, NitroController.GpuLevelMemMax)));
            }
            result = s.Api.SetGpuLevels(parsed);
            Console.WriteLine("GPU overclock levels: " + string.Join("  ", parsed.Select((l, i) => $"L{i} {Offsets(l)}")));
        }
        Output.Gpu(result);
        return 0;
    }

    public static int NitroKey(CliSession s, Args args)
    {
        var value = args.Next();
        args.End();
        if (value is null)
        {
            Console.WriteLine(NitroKeyActions.ToText(s.Api.Status().NitroKeyAction));
            if (!s.UsesDaemon)
                Console.WriteLine("(nitropowerd listens to the key; it is not running)");
            return 0;
        }
        s.RequireWrite("the Nitro key");
        if (value == "press")
        {
            var change = s.Api.PressNitroKey();
            if (change is null)
                Console.WriteLine("Nitro key action is gui or none: nothing to change.");
            else
                Change(change);
            return 0;
        }
        if (NitroKeyActions.Parse(value) is null)
            throw new UsageException("nitro-key takes turbo, cycle, gui, none or press");
        s.Api.SetNitroKeyAction(value);
        Console.WriteLine($"Nitro key: {NitroKeyActions.ToText(NitroKeyActions.Parse(value)!.Value)}");
        return 0;
    }

    public static int ModeKey(CliSession s, Args args)
    {
        var value = args.Next();
        args.End();
        if (value is null)
        {
            Console.WriteLine(s.Api.Status().DriverModeKey switch
            {
                ModeKeyBehavior.Cycle => "cycle",
                ModeKeyBehavior.TurboToggle => "turbo",
                _ => "unknown (linuwu_sense not loaded)",
            });
            return 0;
        }
        if (value is not ("cycle" or "turbo"))
            throw new UsageException("mode-key takes cycle or turbo");
        s.RequireWrite("the mode key setting");
        s.Api.SetModeKey(value);
        Console.WriteLine($"Firmware mode key: {value} (models whose key arrives as WMI event 7)");
        return 0;
    }

    public static int Keyboard(CliSession s, Args args)
    {
        var action = args.Next();
        if (action is null)
        {
            args.End();
            if (s.Api.Status().Keyboard is not { } kb)
            {
                Console.WriteLine("No four-zone RGB keyboard (four_zoned_kb) on this model or driver.");
                return 0;
            }
            Row("Effect", Effect(kb.Effect));
            Row("Zones", Zones(kb.Zones));
            return 0;
        }
        return action switch
        {
            "effect" => KeyboardEffectCommand(s, args),
            "zones" => KeyboardZonesCommand(s, args),
            _ => throw new UsageException("keyboard takes effect or zones"),
        };
    }

    private static int KeyboardEffectCommand(CliSession s, Args args)
    {
        var name = args.Require("effect name");
        if (int.TryParse(name, out _) || !Enum.TryParse<KeyboardEffect>(name, ignoreCase: true, out var effect) ||
            !Enum.IsDefined(effect))
            throw new UsageException($"unknown effect '{name}'; effects: {string.Join(" ", Enum.GetNames<KeyboardEffect>().Select(n => n.ToLowerInvariant()))}");
        var current = s.Api.Status().Keyboard?.Effect;
        var color = args.Option("--color") is { } hex
            ? Rgb.TryParseHex(hex) ?? throw new UsageException("--color takes RRGGBB")
            : current?.Color is { } cc && cc != Rgb.Black ? cc : new Rgb(255, 255, 255);
        var direction = args.Option("--direction") is { } d
            ? d.ToLowerInvariant() switch
            {
                "rtl" or "right-to-left" or "1" => KeyboardDirection.RightToLeft,
                "ltr" or "left-to-right" or "2" => KeyboardDirection.LeftToRight,
                _ => throw new UsageException("--direction takes rtl or ltr"),
            }
            : current?.Direction is { } cd and not KeyboardDirection.None ? cd : KeyboardDirection.RightToLeft;
        var state = new KeyboardEffectState(
            effect,
            args.IntOption("--speed", 0, KeyboardEffectState.MaxSpeed) ?? (current is { Speed: > 0 } ? current.Speed : 4),
            args.IntOption("--brightness", 0, KeyboardEffectState.MaxBrightness) ?? current?.Brightness ?? 100,
            direction,
            color).Normalize();
        args.End();
        s.RequireWrite("the keyboard lighting");
        s.Api.SetKeyboardEffect(state);
        Console.WriteLine($"Keyboard: {Effect(state)}");
        return 0;
    }

    private static int KeyboardZonesCommand(CliSession s, Args args)
    {
        var words = args.Rest();
        if (words.Count is not (1 or PerZoneColors.ZoneCount))
            throw new UsageException("keyboard zones takes one color (all zones) or four colors, as RRGGBB");
        var colors = words.Select(w => Rgb.TryParseHex(w) ?? throw new UsageException($"'{w}' is not RRGGBB")).ToList();
        if (colors.Count == 1)
            colors = Enumerable.Repeat(colors[0], PerZoneColors.ZoneCount).ToList();
        var brightness = args.IntOption("--brightness", 0, KeyboardEffectState.MaxBrightness)
                         ?? s.Api.Status().Keyboard?.Zones?.Brightness ?? 100;
        args.End();
        var zones = PerZoneColors.FromZones(colors, brightness);
        s.RequireWrite("the keyboard lighting");
        s.Api.SetKeyboardZones(zones);
        Console.WriteLine($"Keyboard zones: {Zones(zones)}");
        return 0;
    }

    public static int Preset(CliSession s, Args args)
    {
        var action = args.Next() ?? "list";
        switch (action)
        {
            case "list":
                args.End();
                var names = s.Api.ListPresets();
                Console.WriteLine(names.Count == 0 ? "No presets." : string.Join("\n", names));
                return 0;
            case "show":
            {
                var name = args.Require("preset name");
                args.End();
                var preset = s.Api.GetPreset(name) ?? throw new NitroException($"no preset '{name}'");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(preset,
                    new System.Text.Json.JsonSerializerOptions(NitroApiProtocol.Json) { WriteIndented = true }));
                return 0;
            }
            case "save":
            {
                var name = args.Require("preset name");
                args.End();
                s.RequireWrite("saving a preset");
                s.Api.SavePreset(name);
                Console.WriteLine($"Saved preset '{name}'.");
                return 0;
            }
            case "load" or "apply":
            {
                var name = args.Require("preset name");
                args.End();
                s.RequireWrite("loading a preset");
                var results = s.Api.LoadPreset(name);
                foreach (var r in results)
                    Console.WriteLine(r.Ok ? $"  ok      {r.Setting}" : $"  failed  {r.Setting}: {r.Error}");
                var failed = results.Count(r => !r.Ok);
                Console.WriteLine(failed == 0 ? $"Preset '{name}' applied." : $"Preset '{name}' applied with {failed} error(s).");
                return failed == 0 ? 0 : 1;
            }
            case "delete":
            {
                var name = args.Require("preset name");
                args.End();
                s.RequireWrite("deleting a preset");
                Console.WriteLine(s.Api.DeletePreset(name) ? $"Deleted preset '{name}'." : $"No preset '{name}'.");
                return 0;
            }
            default:
                throw new UsageException("preset takes list, show, save, load or delete");
        }
    }
}
