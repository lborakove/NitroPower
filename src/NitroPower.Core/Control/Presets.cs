using System.Text.Json;
using System.Text.Json.Serialization;
using NitroPower.Core.Hardware;

namespace NitroPower.Core.Control;

/// <summary>
/// A named set of settings, applied in one go. Null fields are left alone.
/// Keyboard values use the driver's own text formats.
/// </summary>
public sealed record Preset
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? Mode { get; init; }
    public bool? Turbo { get; init; }

    /// <summary>"auto", "max", "cpu,gpu" percent or "curve".</summary>
    public string? Fans { get; init; }

    /// <summary>The curves, when <see cref="Fans"/> is "curve".</summary>
    public FanCurveSettings? FanCurve { get; init; }

    public bool? GpuOverclock { get; init; }
    public bool? BatteryLimiter { get; init; }
    public int? UsbCharging { get; init; }
    public bool? KeyboardTimeout { get; init; }
    public bool? LcdOverdrive { get; init; }
    public bool? BootSound { get; init; }

    /// <summary>four_zone_mode text: "mode,speed,brightness,direction,r,g,b".</summary>
    public string? KeyboardEffect { get; init; }

    /// <summary>per_zone_mode text: "rrggbb,rrggbb,rrggbb,rrggbb,brightness".</summary>
    public string? KeyboardZones { get; init; }
}

public sealed record PresetResult(string Setting, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Preset files, one JSON per name (default /etc/nitropower/presets).</summary>
public sealed class PresetStore(string directory)
{
    public const string DefaultDirectory = "/etc/nitropower/presets";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; } = directory;

    /// <summary>Letters, digits, '-', '_' and '.', not starting with '.', up to 64 characters.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 64 && name[0] != '.' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private string FileFor(string name) =>
        IsValidName(name)
            ? Path.Join(Directory, name + ".json")
            : throw new NitroException($"invalid preset name '{name}' (letters, digits, - _ .)");

    public IReadOnlyList<string> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(IsValidName)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    public Preset? Load(string name)
    {
        var file = FileFor(name);
        if (!File.Exists(file))
            return null;
        try
        {
            var preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(file), Json);
            if (preset is null || preset.SchemaVersion != Preset.CurrentSchemaVersion)
                throw new NitroException($"preset '{name}' has an unsupported format");
            return preset;
        }
        catch (JsonException e)
        {
            throw new NitroException($"preset '{name}' is not valid JSON: {e.Message}");
        }
    }

    /// <summary>The daemon writes presets as root for any group member, so their number is capped.</summary>
    public const int MaxPresets = 100;

    public void Save(string name, Preset preset)
    {
        var file = FileFor(name);
        if (!File.Exists(file) && List().Count >= MaxPresets)
            throw new NitroException($"too many presets (at most {MaxPresets}); delete one first");
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(preset, Json) + "\n");
        File.Move(tmp, file, overwrite: true);
    }

    public bool Delete(string name)
    {
        var file = FileFor(name);
        if (!File.Exists(file))
            return false;
        File.Delete(file);
        return true;
    }
}

public static class Presets
{
    /// <summary>The current settings. With turbo on, records the mode and fans
    /// underneath plus Turbo = true, so applying it rebuilds the same state.</summary>
    public static Preset Capture(NitroController c)
    {
        var sense = c.Sense;
        var state = c.State;
        var turbo = c.IsTurboActive;
        var effect = c.Keyboard?.GetEffect();
        // Per-zone colors are applied as a static effect, so a static effect
        // is saved as zones (which also carry the colors of each zone).
        var zones = effect is null or { Effect: Hardware.KeyboardEffect.Static } ? c.Keyboard?.GetZones() : null;

        return new Preset
        {
            Mode = turbo ? state.Turbo!.PreviousMode : c.CurrentMode?.Id,
            Turbo = c.Profile is null ? null : turbo,
            Fans = turbo ? state.Turbo!.PreviousFans : c.FanChoice,
            FanCurve = (turbo ? state.Turbo!.PreviousFans : state.Fans) == Profiles.FanSetting.CurveText
                ? state.EffectiveFanCurve
                : null,
            GpuOverclock = c.Machine.GpuOcEntry is null ? null : !state.GpuOverclockDisabled,
            BatteryLimiter = sense?.GetFlag(NitroSenseDevice.BatteryLimiter),
            UsbCharging = sense?.GetUsbCharging(),
            KeyboardTimeout = sense?.GetFlag(NitroSenseDevice.KeyboardTimeout),
            LcdOverdrive = sense?.GetFlag(NitroSenseDevice.LcdOverdrive),
            BootSound = sense?.GetFlag(NitroSenseDevice.BootSound),
            KeyboardEffect = zones is null ? effect?.ToSysfs() : null,
            KeyboardZones = zones?.ToSysfs(),
        };
    }

    /// <summary>Applies every field that is set; one failure does not stop the rest.</summary>
    public static IReadOnlyList<PresetResult> Apply(NitroController c, Preset p)
    {
        var results = new List<PresetResult>();

        void Step(string setting, Action action)
        {
            try
            {
                action();
                results.Add(new PresetResult(setting, null));
            }
            catch (Exception e) when (e is NitroException or IOException or UnauthorizedAccessException
                                          or ArgumentException or FormatException)
            {
                results.Add(new PresetResult(setting, e.Message));
            }
        }

        // Leave turbo first so that the mode and fans below are not undone by it.
        if (c.IsTurboActive && (p.Turbo is not null || p.Mode is not null || p.Fans is not null))
            Step("turbo off", () => c.SetTurbo(false));
        if (p.GpuOverclock is { } oc)
            Step("gpuOverclock", () => c.SetGpuOverclockEnabled(oc));
        if (p.Mode is { } mode)
            Step("mode", () => c.SetMode(mode));
        if (p.FanCurve is { } curve)
            Step("fanCurve", () => c.SetFanCurve(curve, activate: false));
        if (p.Fans is { } fans)
            Step("fans", () => c.SetFanChoice(fans));
        if (p.Turbo == true)
            Step("turbo", () => c.SetTurbo(true));
        if (p.BatteryLimiter is { } limiter)
            Step("batteryLimiter", () => c.SetFeature(NitroSenseDevice.BatteryLimiter, limiter));
        if (p.UsbCharging is { } usb)
            Step("usbCharging", () => c.SetUsbCharging(usb));
        if (p.KeyboardTimeout is { } timeout)
            Step("keyboardTimeout", () => c.SetFeature(NitroSenseDevice.KeyboardTimeout, timeout));
        if (p.LcdOverdrive is { } lcd)
            Step("lcdOverdrive", () => c.SetFeature(NitroSenseDevice.LcdOverdrive, lcd));
        if (p.BootSound is { } sound)
            Step("bootSound", () => c.SetFeature(NitroSenseDevice.BootSound, sound));
        if (p.KeyboardEffect is { } effect)
            Step("keyboardEffect", () => c.SetKeyboardEffect(
                KeyboardEffectState.TryParse(effect) ?? throw new FormatException($"bad keyboard effect '{effect}'")));
        if (p.KeyboardZones is { } zones)
            Step("keyboardZones", () => c.SetKeyboardZones(
                PerZoneColors.TryParse(zones) ?? throw new FormatException($"bad keyboard zones '{zones}'")));
        return results;
    }
}
