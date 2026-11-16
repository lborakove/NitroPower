using NitroPower.Core.IO;

namespace NitroPower.Core.Hardware;

/// <param name="Address">PCI address, e.g. "0000:01:00.0".</param>
/// <param name="PciId">"vendor:device" in lowercase hex, e.g. "10de:28a0".</param>
/// <param name="Subsystem">"subvendor:subdevice", e.g. "1025:171e".</param>
/// <param name="ClassCode">PCI class, e.g. 0x030000 (VGA) or 0x030200 (3D).</param>
public sealed record PciDevice(string Address, string PciId, string Subsystem, int ClassCode)
{
    public const string NvidiaVendor = "10de";

    public bool IsDisplayController => ClassCode >> 16 == 0x03;
    public bool IsNvidia => PciId.StartsWith(NvidiaVendor + ":", StringComparison.Ordinal);
}

public static class PciDevices
{
    public const string BasePath = "/sys/bus/pci/devices";

    public static IReadOnlyList<PciDevice> Read(ISysfs fs)
    {
        var devices = new List<PciDevice>();
        foreach (var dir in fs.ListDirectories(BasePath))
        {
            var vendor = Hex(fs.ReadText($"{dir}/vendor"));
            var device = Hex(fs.ReadText($"{dir}/device"));
            if (vendor is null || device is null)
                continue;
            var subVendor = Hex(fs.ReadText($"{dir}/subsystem_vendor")) ?? "0000";
            var subDevice = Hex(fs.ReadText($"{dir}/subsystem_device")) ?? "0000";
            var classText = fs.ReadText($"{dir}/class");
            var classCode = classText is not null && classText.StartsWith("0x", StringComparison.Ordinal)
                ? Convert.ToInt32(classText[2..], 16)
                : 0;
            devices.Add(new PciDevice(Path.GetFileName(dir), $"{vendor}:{device}", $"{subVendor}:{subDevice}", classCode));
        }
        return devices;
    }

    /// <summary>Discrete NVIDIA display controllers.</summary>
    public static IEnumerable<PciDevice> NvidiaGpus(IEnumerable<PciDevice> devices) =>
        devices.Where(d => d.IsNvidia && d.IsDisplayController);

    /// <summary>"0x10de" → "10de" (lowercase, 4 digits).</summary>
    private static string? Hex(string? value)
    {
        if (value is null || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.Length < 3)
            return null;
        return value[2..].ToLowerInvariant().PadLeft(4, '0');
    }
}
