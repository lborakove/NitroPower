using NitroPower.Core.IO;
using NitroPower.Core.Policy;
using NitroPower.Gui.Controls;
using NitroPower.Gui.Localization;
using NitroPower.Gui.Services;

namespace NitroPower.Gui.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("nitropower-gui-test-").FullName;
    public Sysfs Fs => new(Root);

    public TempTree File(string path, string content)
    {
        var physical = Path.Join(Root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        System.IO.File.WriteAllText(physical, content + "\n");
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class ServiceTests
{
    private const string Bat = "/sys/class/power_supply/BAT1";

    [Fact]
    public void Battery_facts_from_energy_counters()
    {
        using var t = new TempTree();
        t.File($"{Bat}/type", "Battery").File($"{Bat}/status", "Discharging").File($"{Bat}/capacity", "50")
            .File($"{Bat}/energy_now", "26000000").File($"{Bat}/energy_full", "52000000")
            .File($"{Bat}/energy_full_design", "57500000").File($"{Bat}/power_now", "13000000")
            .File($"{Bat}/voltage_now", "15800000").File($"{Bat}/cycle_count", "87")
            .File("/sys/class/power_supply/AC0/type", "Mains").File("/sys/class/power_supply/AC0/online", "0");

        var b = BatteryFacts.Read(t.Fs)!;
        Assert.Equal(("BAT1", 50, 87), (b.Name, b.Percent, b.Cycles));
        Assert.Equal(26.0, b.EnergyNowWh!.Value, 3);
        Assert.Equal(90.4, b.HealthPercent!.Value, 1);
        Assert.Equal(TimeSpan.FromHours(2), b.TimeLeft);
    }

    [Fact]
    public void Battery_facts_from_charge_counters_and_peripherals_ignored()
    {
        using var t = new TempTree();
        t.File("/sys/class/power_supply/hidpp_battery_0/type", "Battery")
            .File("/sys/class/power_supply/hidpp_battery_0/scope", "Device")
            .File($"{Bat}/type", "Battery").File($"{Bat}/status", "Charging").File($"{Bat}/capacity", "80")
            .File($"{Bat}/charge_now", "3000000").File($"{Bat}/charge_full", "3500000")
            .File($"{Bat}/charge_full_design", "4000000").File($"{Bat}/current_now", "1000000")
            .File($"{Bat}/voltage_now", "15000000").File($"{Bat}/voltage_min_design", "15000000");

        var b = BatteryFacts.Read(t.Fs)!;
        Assert.Equal("BAT1", b.Name);
        Assert.Equal(45.0, b.EnergyNowWh!.Value, 3);
        Assert.Equal(87.5, b.HealthPercent!.Value, 1);
        Assert.Equal(15.0, b.RateW!.Value, 3);
        Assert.Equal(TimeSpan.FromHours(0.5), b.TimeLeft); // (52.5 - 45) / 15
        Assert.Null(b.Cycles);
    }

    [Fact]
    public void System_facts_read_proc_and_os_release()
    {
        using var t = new TempTree();
        t.File("/etc/os-release", "NAME=Ubuntu\nPRETTY_NAME=\"Ubuntu 25.10\"")
            .File("/proc/cpuinfo", "processor\t: 0\nmodel name\t: Test CPU @ 3GHz\n")
            .File("/proc/meminfo", "MemTotal:       16000000 kB\nMemFree: 1 kB")
            .File("/proc/sys/kernel/osrelease", "6.17.0-test")
            .File("/sys/module/nvidia/version", "580.95.05");
        var f = SystemFacts.Read(t.Fs);
        Assert.Equal(("Ubuntu 25.10", "Test CPU @ 3GHz", "6.17.0-test", "580.95.05"), (f.Distribution, f.Cpu, f.Kernel, f.NvidiaDriver));
        Assert.Equal(15.26, f.MemoryGiB!.Value, 2);
    }

    [Fact]
    public void Driver_facts_list_the_module_parameters()
    {
        using var t = new TempTree();
        Assert.False(DriverFacts.Read(t.Fs).Loaded);
        t.File("/sys/module/linuwu_sense/srcversion", "ABC123")
            .File("/sys/module/linuwu_sense/parameters/nitro_v4", "N")
            .File("/sys/module/linuwu_sense/parameters/cycle_gaming_thermal_profile", "Y");
        var d = DriverFacts.Read(t.Fs);
        Assert.True(d.Loaded);
        Assert.Equal("ABC123", d.SrcVersion);
        Assert.Equal(["cycle_gaming_thermal_profile=Y", "nitro_v4=N"], d.Parameters.Select(p => $"{p.Key}={p.Value}"));
    }

    [Fact]
    public void Curve_editor_evaluates_like_the_daemon()
    {
        var curve = FanCurve.Default;
        for (var temp = 10.0; temp <= 105; temp += 2.5)
            Assert.Equal(curve.Evaluate(temp), (int)Math.Round(FanCurveEditor.Evaluate(curve.Points, temp)));
    }

    [Fact]
    public void Settings_round_trip_and_survive_bad_files()
    {
        using var t = new TempTree();
        var path = Path.Join(t.Root, "gui.json");
        Assert.Equal(new AppSettings(), AppSettings.Load(path));
        var s = new AppSettings { Language = "pt", Theme = "light", CloseToTray = false, RefreshMs = 2000 };
        s.Save(path);
        Assert.Equal(s, AppSettings.Load(path));
        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), AppSettings.Load(path));
        File.WriteAllText(path, """{"refreshMs": 7}""");
        Assert.Equal(1000, AppSettings.Load(path).RefreshMs);
    }
}
