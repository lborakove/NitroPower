using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Input;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

/// <summary>The pieces nitropowerd runs: GPU overclock service, fan curve, Nitro key.</summary>
public class DaemonPartsTests
{
    private static readonly GpuClockOffsets Extreme = new(100, 200);

    [Fact]
    public void Gpu_service_leaves_the_gpu_alone_when_nothing_is_wanted_or_applied()
    {
        using var m = new FakeNitro("balanced");
        var service = new GpuOverclockService(m.Controller(), m.Gpu0);

        Assert.Equal(GpuApplyStatus.Unchanged, service.Sync().Status); // reads once to learn the state
        var reads = m.Gpu0.Reads;
        Assert.Equal(GpuApplyStatus.Unchanged, service.Sync().Status);
        Assert.Equal(reads, m.Gpu0.Reads); // zero wanted, zero applied: NVML not touched
        Assert.Equal(0, m.Gpu0.Writes);
    }

    [Fact]
    public void Gpu_service_applies_and_reapplies_after_a_reset()
    {
        using var m = new FakeNitro("performance");
        var service = new GpuOverclockService(m.Controller(), m.Gpu0);

        Assert.Equal(GpuApplyStatus.Applied, service.Sync().Status);
        Assert.Equal(Extreme, m.Gpu0.Offsets);
        Assert.Equal(GpuApplyStatus.Unchanged, service.Sync().Status);

        m.Gpu0.Offsets = GpuClockOffsets.None; // GPU reset / resume from suspend
        Assert.Equal(GpuApplyStatus.Applied, service.Sync().Status);
        Assert.Equal(Extreme, m.Gpu0.Offsets);

        m.Gpu0.IsAwake = false;
        Assert.Equal(GpuApplyStatus.Suspended, service.Sync().Status);

        m.Gpu0.IsAwake = true;
        service.Reset();
        Assert.Equal(GpuClockOffsets.None, m.Gpu0.Offsets);
    }

    [Fact]
    public void Gpu_service_keeps_the_battery_hysteresis_between_calls()
    {
        using var m = new FakeNitro("performance", battery: 35);
        var service = new GpuOverclockService(m.Controller(), m.Gpu0);
        Assert.Equal(Extreme, service.Sync().Offsets);

        m.SetPower(onAc: true, battery: 25);
        Assert.Equal(GpuClockOffsets.None, service.Sync().Offsets);
        m.SetPower(onAc: true, battery: 35); // still below the 40% resume point
        Assert.Equal(GpuClockOffsets.None, service.Sync().Offsets);
        m.SetPower(onAc: true, battery: 40);
        Assert.Equal(Extreme, service.Sync().Offsets);
    }

    [Fact]
    public void Fan_curve_engine_writes_only_on_changes_and_pauses_for_turbo()
    {
        using var m = new FakeNitro("balanced");
        var c = m.Controller();
        (double? Cpu, double? Gpu) temps = (60, 50);
        var engine = new FanCurveEngine(c, () => temps);

        Assert.Null(engine.Tick()); // fan choice is not "curve"
        Assert.False(engine.IsActive);

        c.SetFanCurve(new FanCurveSettings([new(40, 20), new(80, 100)], [new(40, 30), new(80, 90)]));
        Assert.Equal(new FanSpeeds(60, 45), engine.Tick());
        Assert.Equal("60,45", m.Fans);
        Assert.Null(engine.Tick()); // same temperatures: no write

        temps = (70, 50);
        Assert.Equal(new FanSpeeds(80, 45), engine.Tick());

        c.SetTurbo(true);
        Assert.Equal("100,100", m.Fans);
        Assert.Null(engine.Tick());
        Assert.False(engine.IsActive);

        c.SetTurbo(false); // restores the curve, not fixed speeds
        Assert.Equal("curve", c.State.Fans);
        Assert.NotNull(engine.Tick());
        Assert.True(engine.IsActive);

        temps = (null, null);
        Assert.Equal(FanSpeeds.Auto, engine.Tick()); // blind: firmware takes over
    }

    [Fact]
    public void Fan_choice_is_stored_and_unsafe_curves_are_refused()
    {
        using var m = new FakeNitro();
        var c = m.Controller();

        c.SetFanChoice("max");
        Assert.Equal(("100,100", "max"), (m.Fans, c.State.Fans));
        c.SetFanChoice("35,45");
        Assert.Equal("35,45", c.State.Fans);
        c.SetFanChoice("curve");
        Assert.Equal("curve", c.FanChoice);

        Assert.Throws<NitroException>(() => c.SetFanCurve(new FanCurveSettings([new(40, 50), new(60, 30)], [new(40, 30), new(80, 90)])));
        Assert.Throws<NitroException>(() => c.SetFanChoice("loud"));
    }

    [Fact]
    public void Nitro_key_runs_the_configured_action()
    {
        using var m = new FakeNitro("balanced");
        var c = m.Controller();

        Assert.Equal(NitroKeyAction.Turbo, c.NitroKeyAction);
        Assert.True(c.PressNitroKey()!.Turbo);
        Assert.False(c.PressNitroKey()!.Turbo);

        c.SetNitroKeyAction(NitroKeyAction.Cycle);
        Assert.Equal("performance", c.PressNitroKey()!.To);

        c.SetNitroKeyAction(NitroKeyAction.Gui);
        Assert.Null(c.PressNitroKey());
        Assert.Equal("performance", m.Mode);
    }

    // From /proc/bus/input/devices on the ANV15-52, trimmed
    private const string ProcDevices = """
        I: Bus=0011 Vendor=0001 Product=0001 Version=ab83
        N: Name="AT Translated Set 2 keyboard"
        H: Handlers=sysrq kbd event3 leds
        B: KEY=180000 10000 c000000000000 0 0 700f02000007 ff802078f870f401 febfffdfffcfffff fffffffffffffffe

        I: Bus=0003 Vendor=046d Product=4062 Version=0111
        N: Name="Logitech K850"
        H: Handlers=sysrq kbd event6 leds
        B: KEY=3f00733fff 0 0 483ffff17aff32d bfd4444600000000 1 130ff38b17d007 ffff7bfad941dfff ffbeffdfffefffff fffffffffffffffe

        I: Bus=0019 Vendor=0000 Product=0000 Version=0000
        N: Name="Video Bus"
        H: Handlers=kbd event10
        B: KEY=3e000b00000000 0 0 0

        I: Bus=0019 Vendor=0000 Product=0000 Version=0000
        N: Name="Acer WMI hotkeys"
        H: Handlers=rfkill kbd event4
        B: KEY=1c0000 0 0 0 0 1600800000c00 300400 10000000000000 0
        """;

    [Fact]
    public void Finds_the_internal_devices_that_carry_the_nitro_key()
    {
        var all = InputDevices.WithKey(ProcDevices, 148, null);
        Assert.Equal(["event3", "event4"], all.Select(d => d.EventNode)); // not the K850 nor the Video Bus
        Assert.Single(InputDevices.WithKey(ProcDevices, 1, null), d => d.Name == "Logitech K850"); // KEY_ESC

        var internalOnly = InputDevices.WithKey(ProcDevices, 148, NitroKeyInfo.DefaultDevices.ToList());
        Assert.Equal(["AT Translated Set 2 keyboard", "Acer WMI hotkeys"], internalOnly.Select(d => d.Name));

        Assert.False(InputDevices.HasBit("3e000b00000000 0 0 0", 148));
        Assert.True(InputDevices.HasBit("300400 0 0", 148)); // word 2, bit 20
    }

    [Fact]
    public void Parses_input_events()
    {
        var raw = new byte[InputEvent.Size];
        BitConverter.GetBytes((ushort)1).CopyTo(raw, 16);   // EV_KEY
        BitConverter.GetBytes((ushort)148).CopyTo(raw, 18); // KEY_PROG1
        BitConverter.GetBytes(1).CopyTo(raw, 20);           // press

        var e = InputEvent.Parse(raw);
        Assert.True(e.IsPressOf(148));
        Assert.False(e.IsPressOf(149));
        Assert.False((e with { Value = 2 }).IsPressOf(148)); // autorepeat
    }

    [Theory]
    [InlineData(0x20, 8)]   // EV_CNT: 4 bytes of bits, but the kernel wants a whole long
    [InlineData(0x300, 96)] // KEY_CNT
    [InlineData(1, 8)]
    public void Evdev_masks_are_whole_longs(int bits, int bytes) =>
        Assert.Equal(bytes, EvdevFilter.MaskBytes(bits));

    [Theory]
    [InlineData("curve", "curve")]
    [InlineData("auto", "auto")]
    [InlineData("100,100", "max")]
    [InlineData("0,0", "auto")]
    [InlineData("30,40", "30,40")]
    public void Fan_setting_text_round_trip(string input, string expected) =>
        Assert.Equal(expected, FanSetting.Parse(input).ToText());
}
