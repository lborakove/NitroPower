using NitroPower.Core.IO;

namespace NitroPower.Core.Profiles;

/// <summary>SMBIOS identity from /sys/class/dmi/id (only world-readable fields).</summary>
public sealed record DmiInfo(IReadOnlyDictionary<string, string> Fields)
{
    public const string BasePath = "/sys/class/dmi/id";

    private static readonly string[] Keys =
        ["sys_vendor", "product_name", "product_family", "product_version", "board_name", "bios_version", "bios_date"];

    public string? this[string key] => Fields.TryGetValue(key, out var v) ? v : null;

    public static DmiInfo Read(ISysfs fs)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            var value = fs.ReadText($"{BasePath}/{key}");
            if (!string.IsNullOrEmpty(value))
                fields[key] = value;
        }
        return new DmiInfo(fields);
    }
}
