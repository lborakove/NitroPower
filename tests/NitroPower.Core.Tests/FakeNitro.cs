using System.Globalization;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

internal sealed class FakeGpu : IGpuControl
{
    public bool IsAwake { get; set; } = true;
    public GpuClockOffsets Offsets { get; set; } = GpuClockOffsets.None;
    public int Writes { get; private set; }
    public int Reads { get; private set; }

    public GpuClockOffsets GetOffsets()
    {
        Reads++;
        return Offsets;
    }

    public void SetOffsets(GpuClockOffsets offsets)
    {
        Offsets = offsets;
        Writes++;
    }
}

/// <summary>An ANV15-52 with linuwu_sense loaded, an RTX 4060 and AC power, in a temporary tree.</summary>
internal sealed class FakeNitro : IDisposable
{
    public const string Ns = "/sys/devices/platform/acer-wmi/nitro_sense";
    private const string Gpu = "/sys/bus/pci/devices/0000:01:00.0";

    public TestTree Tree { get; } = new();
    public FakeGpu Gpu0 { get; } = new();
    public string StatePath => Path.Join(Tree.Root, "var-lib-nitropower", "state.json");

    public FakeNitro(string mode = "balanced", bool onAc = true, int battery = 80, string fans = "0,0")
    {
        Tree.File("/sys/class/dmi/id/sys_vendor", "Acer")
            .File("/sys/class/dmi/id/product_name", "Nitro ANV15-52")
            .File(PlatformProfile.CurrentPath, mode)
            .File(PlatformProfile.ChoicesPath, "low-power quiet balanced performance")
            .File($"{Ns}/fan_speed", fans)
            .File($"{Ns}/battery_limiter", "0")
            .File($"{Ns}/battery_calibration", "0")
            .File($"{Ns}/usb_charging", "0")
            .File($"{Ns}/backlight_timeout", "1")
            .File($"{Ns}/lcd_override", "0")
            .File($"{Ns}/boot_animation_sound", "1")
            .File("/sys/class/power_supply/ACAD/type", "Mains")
            .File("/sys/class/power_supply/BAT1/type", "Battery")
            .File($"{Gpu}/vendor", "0x10de")
            .File($"{Gpu}/device", "0x28a0")
            .File($"{Gpu}/subsystem_vendor", "0x1025")
            .File($"{Gpu}/subsystem_device", "0x171e")
            .File($"{Gpu}/class", "0x030000");
        SetPower(onAc, battery);
    }

    public FakeNitro WithKeyboard(string effect = "0,0,100,0,255,255,255", string zones = "ffffff,ffffff,ffffff,ffffff,100")
    {
        Tree.File(FourZoneKeyboard.EffectPath, effect).File(FourZoneKeyboard.ZonesPath, zones);
        return this;
    }

    public void SetPower(bool onAc, int battery = 80)
    {
        Tree.File("/sys/class/power_supply/ACAD/online", onAc ? "1" : "0")
            .File("/sys/class/power_supply/BAT1/capacity", battery.ToString(CultureInfo.InvariantCulture));
    }

    public NitroController Controller()
    {
        var catalog = ProfileCatalog.Load([TestTree.RepoProfilesDirectory()]);
        var machine = MachineInfo.Detect(Tree.Fs, catalog);
        return new NitroController(Tree.Fs, machine, new StateStore(StatePath), Gpu0);
    }

    public string Mode => Tree.Read(PlatformProfile.CurrentPath).Trim();

    public string Fans => Tree.Read($"{Ns}/fan_speed").Trim();

    public string Attr(string name) => Tree.Read($"{Ns}/{name}").Trim();

    public void Dispose() => Tree.Dispose();
}
