using System.Globalization;
using NitroPower.Core.IO;

namespace NitroPower.Core.Monitoring;

/// <summary>Lookup of /sys/class/hwmon devices by their "name" attribute.</summary>
public sealed class Hwmon(ISysfs fs)
{
    public const string BasePath = "/sys/class/hwmon";

    /// <summary>Path of the first hwmon device with this name, or null.</summary>
    public string? Find(string name) =>
        fs.ListDirectories(BasePath).FirstOrDefault(d => fs.ReadText($"{d}/name") == name);

    /// <summary>tempN_input in °C (the kernel reports millidegrees).</summary>
    public double? Temperature(string? device, int channel) =>
        device is null ? null : Milli(fs.ReadText($"{device}/temp{channel}_input"));

    /// <summary>Temperature of the channel whose label equals <paramref name="label"/>.</summary>
    public double? TemperatureByLabel(string? device, string label)
    {
        if (device is null)
            return null;
        for (var channel = 1; channel <= 64; channel++)
        {
            var path = $"{device}/temp{channel}_label";
            if (!fs.Exists($"{device}/temp{channel}_input"))
                continue;
            if (fs.ReadText(path) == label)
                return Temperature(device, channel);
        }
        return null;
    }

    public int? FanRpm(string? device, int channel) =>
        device is not null &&
        int.TryParse(fs.ReadText($"{device}/fan{channel}_input"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm)
            ? rpm
            : null;

    private static double? Milli(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v / 1000.0 : null;
}
