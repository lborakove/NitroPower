using System.Globalization;
using NitroPower.Core.IO;

namespace NitroPower.Gui.Services;

/// <summary>Facts any user can read without the daemon: kernel, distribution,
/// CPU, memory and NVIDIA driver.</summary>
public sealed record SystemFacts(
    string? Kernel,
    string? Distribution,
    string? Cpu,
    double? MemoryGiB,
    string? NvidiaDriver,
    string? LinuwuVersion)
{
    public static SystemFacts Read(ISysfs fs)
    {
        string? distro = null;
        foreach (var line in (fs.ReadText("/etc/os-release") ?? "").Split('\n'))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                distro = line["PRETTY_NAME=".Length..].Trim('"');
        }

        var cpu = (fs.ReadText("/proc/cpuinfo") ?? "").Split('\n')
            .FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?
            .Split(':', 2)[1].Trim();

        double? mem = null;
        var memLine = (fs.ReadText("/proc/meminfo") ?? "").Split('\n')
            .FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        if (memLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var kb, ..] &&
            long.TryParse(kb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k))
            mem = k / 1024.0 / 1024.0;

        var nvidia = fs.ReadText("/sys/module/nvidia/version");
        var linuwu = fs.ReadText("/sys/module/linuwu_sense/version") ??
                     (fs.ReadText("/sys/module/linuwu_sense/srcversion") is { } src ? $"srcversion {src}" : null);
        return new SystemFacts(fs.ReadText("/proc/sys/kernel/osrelease"), distro, cpu, mem, nvidia, linuwu);
    }
}

/// <summary>The linuwu_sense module: whether it is loaded and its parameters.</summary>
public sealed record DriverFacts(bool Loaded, string? SrcVersion, IReadOnlyList<KeyValuePair<string, string>> Parameters)
{
    public const string ModulePath = "/sys/module/linuwu_sense";

    public static DriverFacts Read(ISysfs fs)
    {
        if (!fs.Exists(ModulePath))
            return new DriverFacts(false, null, []);
        var parameters = fs.ListFiles($"{ModulePath}/parameters")
            .Select(f => new KeyValuePair<string, string>(Path.GetFileName(f), fs.ReadText(f) ?? "?"))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        return new DriverFacts(true, fs.ReadText($"{ModulePath}/srcversion"), parameters);
    }
}

/// <summary>The daemon's journal (readable by members of adm or systemd-journal).</summary>
public static class DaemonLog
{
    public static async Task<string> ReadAsync(int lines = 300)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("journalctl",
                $"-u nitropowerd -n {lines} --no-pager -o short-iso")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return "";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = await p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return (output + error).TrimEnd();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return e.Message;
        }
    }
}

/// <summary>The system battery from /sys/class/power_supply (energy or charge counters).</summary>
public sealed record BatteryFacts(
    string Name,
    string? Status,
    int? Percent,
    double? EnergyNowWh,
    double? EnergyFullWh,
    double? EnergyDesignWh,
    double? RateW,
    double? VoltageV,
    int? Cycles,
    string? Technology,
    string? Model,
    string? Manufacturer)
{
    /// <summary>Full capacity against the design capacity, in percent.</summary>
    public double? HealthPercent => EnergyFullWh is > 0 && EnergyDesignWh is > 0
        ? Math.Min(100, 100 * EnergyFullWh.Value / EnergyDesignWh.Value)
        : null;

    /// <summary>Time to empty (discharging) or to full (charging).</summary>
    public TimeSpan? TimeLeft
    {
        get
        {
            if (RateW is not > 0.5 || EnergyNowWh is not { } now)
                return null;
            var hours = Status switch
            {
                "Discharging" => now / RateW.Value,
                "Charging" when EnergyFullWh is { } full => Math.Max(0, full - now) / RateW.Value,
                _ => (double?)null,
            };
            return hours is { } h and < 48 ? TimeSpan.FromHours(h) : null;
        }
    }

    public static BatteryFacts? Read(ISysfs fs)
    {
        foreach (var dir in fs.ListDirectories("/sys/class/power_supply"))
        {
            if (fs.ReadText($"{dir}/type") != "Battery" || (fs.ReadText($"{dir}/scope") ?? "System") != "System")
                continue;
            double? Micro(string name) =>
                long.TryParse(fs.ReadText($"{dir}/{name}"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                    ? v / 1_000_000.0
                    : null;
            int? Int(string name) =>
                int.TryParse(fs.ReadText($"{dir}/{name}"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

            var voltage = Micro("voltage_now");
            var designVoltage = Micro("voltage_min_design") ?? voltage;
            // energy_* in µWh; charge_* in µAh (times the voltage)
            var now = Micro("energy_now") ?? Micro("charge_now") * voltage;
            var full = Micro("energy_full") ?? Micro("charge_full") * designVoltage;
            var design = Micro("energy_full_design") ?? Micro("charge_full_design") * designVoltage;
            var rate = Micro("power_now") ?? (Micro("current_now") is { } a && voltage is { } v ? a * v : null);
            var cycles = Int("cycle_count");
            return new BatteryFacts(Path.GetFileName(dir), fs.ReadText($"{dir}/status"), Int("capacity"),
                now, full, design, rate is { } r ? Math.Abs(r) : null, voltage, cycles is > 0 ? cycles : null,
                fs.ReadText($"{dir}/technology"), fs.ReadText($"{dir}/model_name"), fs.ReadText($"{dir}/manufacturer"));
        }
        return null;
    }
}
