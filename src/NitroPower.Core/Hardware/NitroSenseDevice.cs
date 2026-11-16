using System.Globalization;
using NitroPower.Core.IO;

namespace NitroPower.Core.Hardware;

/// <summary>Fan duty cycles in percent. 0 means automatic (firmware curve),
/// 1 is the minimum and 100 the maximum.</summary>
public readonly record struct FanSpeeds(int Cpu, int Gpu)
{
    public static FanSpeeds Auto => new(0, 0);
    public static FanSpeeds Max => new(100, 100);
    public bool IsAuto => Cpu == 0 && Gpu == 0;

    public static bool IsValid(int percent) => percent is >= 0 and <= 100;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Cpu},{Gpu}");

    public static FanSpeeds? TryParse(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpu) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gpu) ||
            !IsValid(cpu) || !IsValid(gpu))
            return null;
        return new FanSpeeds(cpu, gpu);
    }
}

/// <summary>
/// The nitro_sense (or predator_sense) attribute group of linuwu_sense under
/// /sys/devices/platform/acer-wmi. A read of -1 means the firmware did not
/// answer; it is reported as null (unknown), never as off.
/// </summary>
public sealed class NitroSenseDevice
{
    public const string PlatformPath = "/sys/devices/platform/acer-wmi";
    private static readonly string[] GroupNames = ["nitro_sense", "predator_sense"];

    public const string FanSpeed = "fan_speed";
    public const string BatteryLimiter = "battery_limiter";
    public const string BatteryCalibration = "battery_calibration";
    public const string UsbCharging = "usb_charging";
    public const string KeyboardTimeout = "backlight_timeout";
    public const string LcdOverdrive = "lcd_override";
    public const string BootSound = "boot_animation_sound";

    /// <summary>Battery thresholds the firmware accepts for USB power when off (0 = disabled).</summary>
    public static readonly int[] UsbChargingLevels = [0, 10, 20, 30];

    private readonly ISysfs _fs;

    public string BasePath { get; }

    private NitroSenseDevice(ISysfs fs, string basePath)
    {
        _fs = fs;
        BasePath = basePath;
    }

    public static NitroSenseDevice? Detect(ISysfs fs)
    {
        foreach (var group in GroupNames)
        {
            var path = $"{PlatformPath}/{group}";
            if (fs.Exists(path))
                return new NitroSenseDevice(fs, path);
        }
        return null;
    }

    private string AttributePath(string attribute) => $"{BasePath}/{attribute}";

    public bool Has(string attribute) => _fs.Exists(AttributePath(attribute));

    public string? ReadRaw(string attribute) => _fs.ReadText(AttributePath(attribute));

    public FanSpeeds? GetFanSpeeds() => FanSpeeds.TryParse(ReadRaw(FanSpeed));

    public void SetFanSpeeds(FanSpeeds speeds)
    {
        if (!FanSpeeds.IsValid(speeds.Cpu) || !FanSpeeds.IsValid(speeds.Gpu))
            throw new ArgumentOutOfRangeException(nameof(speeds), speeds, "fan duty must be 0..100");
        _fs.WriteText(AttributePath(FanSpeed), speeds.ToString());
    }

    /// <summary>0/1 attribute as bool; null when missing, -1 or unexpected.</summary>
    public bool? GetFlag(string attribute) => ReadRaw(attribute) switch
    {
        "0" => false,
        "1" => true,
        _ => null,
    };

    public void SetFlag(string attribute, bool value) =>
        _fs.WriteText(AttributePath(attribute), value ? "1" : "0");

    public int? GetUsbCharging() =>
        int.TryParse(ReadRaw(UsbCharging), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) &&
        UsbChargingLevels.Contains(v)
            ? v
            : null;

    public void SetUsbCharging(int batteryThreshold)
    {
        if (!UsbChargingLevels.Contains(batteryThreshold))
            throw new ArgumentOutOfRangeException(nameof(batteryThreshold), batteryThreshold,
                $"must be one of {string.Join(", ", UsbChargingLevels)}");
        _fs.WriteText(AttributePath(UsbCharging),
            batteryThreshold.ToString(CultureInfo.InvariantCulture));
    }
}
