using NitroPower.Core.Hardware;

namespace NitroPower.Core.Tests;

public class HardwareTests
{
    private const string Ns = "/sys/devices/platform/acer-wmi/nitro_sense";

    [Fact]
    public void Platform_profile_reads_and_validates()
    {
        using var tree = new TestTree()
            .File(PlatformProfile.CurrentPath, "balanced")
            .File(PlatformProfile.ChoicesPath, "low-power quiet balanced performance");
        var pp = new PlatformProfile(tree.Fs);

        Assert.True(pp.IsAvailable);
        Assert.Equal("balanced", pp.Current);
        Assert.Equal(4, pp.Choices.Count);

        pp.Set("performance");
        Assert.Equal("performance", tree.Read(PlatformProfile.CurrentPath));
        Assert.Throws<ArgumentException>(() => pp.Set("turbo"));
    }

    [Fact]
    public void Nitro_sense_fan_speeds_and_flags()
    {
        using var tree = new TestTree()
            .File($"{Ns}/fan_speed", "0,0")
            .File($"{Ns}/battery_limiter", "1")
            .File($"{Ns}/lcd_override", "-1")
            .File($"{Ns}/usb_charging", "30");
        var dev = NitroSenseDevice.Detect(tree.Fs);
        Assert.NotNull(dev);

        Assert.True(dev.GetFanSpeeds()!.Value.IsAuto);
        dev.SetFanSpeeds(new FanSpeeds(50, 70));
        Assert.Equal("50,70", tree.Read($"{Ns}/fan_speed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => dev.SetFanSpeeds(new FanSpeeds(101, 0)));

        Assert.True(dev.GetFlag(NitroSenseDevice.BatteryLimiter));
        Assert.Null(dev.GetFlag(NitroSenseDevice.LcdOverdrive)); // -1 = unknown, not "off"
        Assert.Null(dev.GetFlag(NitroSenseDevice.BootSound));    // missing
        dev.SetFlag(NitroSenseDevice.BatteryLimiter, false);
        Assert.Equal("0", tree.Read($"{Ns}/battery_limiter").Trim());

        Assert.Equal(30, dev.GetUsbCharging());
        Assert.Throws<ArgumentOutOfRangeException>(() => dev.SetUsbCharging(25));
    }

    [Fact]
    public void Nitro_sense_absent_when_driver_not_loaded()
    {
        using var tree = new TestTree();
        Assert.Null(NitroSenseDevice.Detect(tree.Fs));
    }

    [Theory]
    [InlineData("50,70", 50, 70)]
    [InlineData(" 0 , 100 ", 0, 100)]
    public void Fan_speeds_parse(string text, int cpu, int gpu) =>
        Assert.Equal(new FanSpeeds(cpu, gpu), FanSpeeds.TryParse(text));

    [Theory]
    [InlineData("")]
    [InlineData("50")]
    [InlineData("50,101")]
    [InlineData("a,b")]
    public void Fan_speeds_reject_garbage(string text) => Assert.Null(FanSpeeds.TryParse(text));

    [Fact]
    public void Power_supplies_ignore_peripheral_batteries()
    {
        using var tree = new TestTree()
            .File("/sys/class/power_supply/ACAD/type", "Mains")
            .File("/sys/class/power_supply/ACAD/online", "1")
            .File("/sys/class/power_supply/BAT1/type", "Battery")
            .File("/sys/class/power_supply/BAT1/capacity", "87")
            .File("/sys/class/power_supply/hidpp_battery_0/type", "Battery")
            .File("/sys/class/power_supply/hidpp_battery_0/scope", "Device")
            .File("/sys/class/power_supply/hidpp_battery_0/capacity", "5");

        Assert.Equal(new PowerState(true, 87), PowerSupplies.Read(tree.Fs));
    }

    [Fact]
    public void Pci_devices_find_the_nvidia_gpu()
    {
        const string gpu = "/sys/bus/pci/devices/0000:01:00.0";
        const string audio = "/sys/bus/pci/devices/0000:01:00.1";
        using var tree = new TestTree()
            .File($"{gpu}/vendor", "0x10de").File($"{gpu}/device", "0x28a0")
            .File($"{gpu}/subsystem_vendor", "0x1025").File($"{gpu}/subsystem_device", "0x171e")
            .File($"{gpu}/class", "0x030000")
            .File($"{audio}/vendor", "0x10de").File($"{audio}/device", "0x22be")
            .File($"{audio}/class", "0x040300");

        var nvidia = PciDevices.NvidiaGpus(PciDevices.Read(tree.Fs)).ToList();

        var only = Assert.Single(nvidia);
        Assert.Equal("10de:28a0", only.PciId);
        Assert.Equal("1025:171e", only.Subsystem);
        Assert.Equal("0000:01:00.0", only.Address);
    }
    [Fact]
    public void Linuwu_module_parameters_and_mode_key()
    {
        using var tree = new TestTree()
            .File($"{LinuwuModule.ParametersPath}/cycle_gaming_thermal_profile", "Y")
            .File($"{LinuwuModule.ParametersPath}/four_zone_kb", "-1");
        var module = new LinuwuModule(tree.Fs);

        Assert.True(module.IsLoaded);
        Assert.True(module.HasFeatureOverrides);
        Assert.Equal(ModeKeyBehavior.Cycle, module.ModeKey);
        module.SetModeKey(ModeKeyBehavior.TurboToggle);
        Assert.Equal("N", tree.Read($"{LinuwuModule.ParametersPath}/cycle_gaming_thermal_profile"));

        using var empty = new TestTree();
        Assert.False(new LinuwuModule(empty.Fs).IsLoaded);
        Assert.Null(new LinuwuModule(empty.Fs).ModeKey);
    }

    [Fact]
    public void Firmware_turbo_state()
    {
        using var tree = new TestTree().File(TurboSense.StatePath, "1");
        Assert.True(TurboSense.Detect(tree.Fs)!.IsOn);
        tree.File(TurboSense.StatePath, "0");
        Assert.False(TurboSense.Detect(tree.Fs)!.IsOn);

        using var empty = new TestTree();
        Assert.Null(TurboSense.Detect(empty.Fs));
    }
}
