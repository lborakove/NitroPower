using System.Globalization;
using System.Text.RegularExpressions;
using NitroPower.Core.IO;

namespace NitroPower.Core.Monitoring;

/// <summary>Whole-CPU usage from consecutive /proc/stat samples.</summary>
public sealed class CpuUsageSampler(ISysfs fs)
{
    public const string StatPath = "/proc/stat";
    private (ulong Busy, ulong Total)? _previous;

    /// <summary>Usage in percent since the previous call; null on the first call.</summary>
    public double? Sample()
    {
        var current = Parse(fs.ReadText(StatPath));
        if (current is null)
            return null;
        var previous = _previous;
        _previous = current;
        if (previous is null)
            return null;
        var total = current.Value.Total - previous.Value.Total;
        var busy = current.Value.Busy - previous.Value.Busy;
        return total == 0 ? 0 : Math.Clamp(100.0 * busy / total, 0, 100);
    }

    /// <summary>First "cpu" line: busy = total - idle - iowait.</summary>
    internal static (ulong Busy, ulong Total)? Parse(string? stat)
    {
        var line = stat?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
            return null;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(f => ulong.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0UL)
            .ToArray();
        if (fields.Length < 5)
            return null;
        ulong total = 0;
        foreach (var f in fields.Take(8)) // user nice system idle iowait irq softirq steal
            total += f;
        var idle = fields[3] + fields[4];
        return (total - idle, total);
    }
}

public static partial class CpuFrequency
{
    public const string CpuPath = "/sys/devices/system/cpu";

    [GeneratedRegex(@"^cpu\d+$")]
    private static partial Regex CpuDirectory();

    /// <summary>Average and maximum current frequency over all cores, in MHz.</summary>
    public static (int Average, int Max)? Read(ISysfs fs)
    {
        var values = fs.ListDirectories(CpuPath)
            .Where(d => CpuDirectory().IsMatch(Path.GetFileName(d)))
            .Select(d => fs.ReadText($"{d}/cpufreq/scaling_cur_freq"))
            .Select(t => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var khz) ? khz / 1000 : -1)
            .Where(mhz => mhz > 0)
            .ToList();
        return values.Count == 0 ? null : ((int)values.Average(), (int)values.Max());
    }
}
