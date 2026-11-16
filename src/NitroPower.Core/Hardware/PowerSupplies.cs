using System.Globalization;
using NitroPower.Core.IO;

namespace NitroPower.Core.Hardware;

public sealed record PowerState(bool OnAc, int? BatteryPercent);

/// <summary>AC adapter and system battery from /sys/class/power_supply.
/// Peripheral batteries (mouse, keyboard: scope=Device) are ignored.</summary>
public static class PowerSupplies
{
    public const string BasePath = "/sys/class/power_supply";

    public static PowerState Read(ISysfs fs)
    {
        var onAc = false;
        int? capacity = null;
        foreach (var supply in fs.ListDirectories(BasePath))
        {
            var type = fs.ReadText($"{supply}/type");
            var scope = fs.ReadText($"{supply}/scope") ?? "System";
            if (scope != "System")
                continue;
            if (type == "Mains" && fs.ReadText($"{supply}/online") == "1")
                onAc = true;
            else if (type == "Battery" &&
                     int.TryParse(fs.ReadText($"{supply}/capacity"), NumberStyles.Integer,
                         CultureInfo.InvariantCulture, out var pct))
                capacity = pct;
        }
        return new PowerState(onAc, capacity);
    }
}
