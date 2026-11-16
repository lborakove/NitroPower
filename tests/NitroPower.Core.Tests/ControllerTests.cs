using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

public class ControllerTests
{
    private static readonly GpuClockOffsets Extreme = new(100, 200);

    [Fact]
    public void Set_mode_writes_the_profile_and_follows_with_the_gpu_overclock()
    {
        using var m = new FakeNitro("balanced");
        var c = m.Controller();

        var change = c.SetMode("performance");

        Assert.Equal(("balanced", "performance"), (change.From, change.To));
        Assert.Equal("performance", m.Mode);
        Assert.Equal(Extreme, m.Gpu0.Offsets);
        Assert.Equal(GpuApplyStatus.Applied, change.Gpu.Status);

        c.SetMode("quiet");
        Assert.Equal(GpuClockOffsets.None, m.Gpu0.Offsets);
        Assert.Equal("quiet", c.State.LastNonTurboMode);
    }

    [Fact]
    public void Ac_only_modes_and_unknown_modes_are_refused()
    {
        using var m = new FakeNitro("balanced", onAc: false);
        var c = m.Controller();

        Assert.Contains("AC", Assert.Throws<NitroException>(() => c.SetMode("performance")).Message);
        Assert.Contains("eco, quiet, balanced, performance",
            Assert.Throws<NitroException>(() => c.SetMode("ludicrous")).Message);
        Assert.Equal("balanced", m.Mode);

        c.SetMode("eco");
        Assert.Equal("low-power", m.Mode);
    }

    [Fact]
    public void Turbo_takes_the_top_mode_and_max_fans_and_restores_them()
    {
        using var m = new FakeNitro("balanced", fans: "40,50");
        var c = m.Controller();

        var on = c.SetTurbo(true);
        Assert.Equal((true, "performance", "100,100"), (on.Turbo, m.Mode, m.Fans));
        Assert.True(c.IsTurboActive);
        Assert.Equal(Extreme, m.Gpu0.Offsets);
        Assert.Equal(new TurboSnapshot("balanced", "40,50"), c.State.Turbo);

        Assert.False(c.SetTurbo(true).Changed);

        var off = c.SetTurbo(false);
        Assert.Equal((false, "balanced", "40,50"), (off.Turbo, m.Mode, m.Fans));
        Assert.False(c.IsTurboActive);
        Assert.Null(c.State.Turbo);
        Assert.Equal(GpuClockOffsets.None, m.Gpu0.Offsets);
        Assert.False(c.SetTurbo(false).Changed);
    }

    [Fact]
    public void Toggle_and_turbo_needs_ac()
    {
        using var m = new FakeNitro("quiet");
        var c = m.Controller();
        Assert.True(c.ToggleTurbo().Turbo);
        Assert.False(c.ToggleTurbo().Turbo);
        Assert.Equal("quiet", m.Mode);

        m.SetPower(onAc: false);
        Assert.Contains("AC", Assert.Throws<NitroException>(() => c.SetTurbo(true)).Message);
    }

    [Fact]
    public void Choosing_another_mode_ends_turbo_and_puts_the_fans_back()
    {
        using var m = new FakeNitro("balanced", fans: "30,30");
        var c = m.Controller();
        c.SetTurbo(true);

        var change = c.SetMode("quiet");

        Assert.Equal("30,30", change.Fans);
        Assert.Equal(("quiet", "30,30"), (m.Mode, m.Fans));
        Assert.Null(c.State.Turbo);
    }

    [Fact]
    public void A_fan_choice_made_during_turbo_is_what_turbo_returns_to()
    {
        using var m = new FakeNitro("balanced");
        var c = m.Controller();
        c.SetTurbo(true);

        c.SetFans(new FanSpeeds(40, 50));
        Assert.True(c.IsTurboActive);
        Assert.Equal("40,50", m.Fans);

        c.SetTurbo(false);
        Assert.Equal(("balanced", "40,50"), (m.Mode, m.Fans));
    }

    [Fact]
    public void Turbo_off_after_the_mode_changed_elsewhere_restores_only_the_fans()
    {
        using var m = new FakeNitro("balanced");
        var c = m.Controller();
        c.SetTurbo(true);
        m.Tree.File(PlatformProfile.CurrentPath, "quiet"); // GNOME menu, driver AC event...

        Assert.False(c.IsTurboActive);
        var off = c.SetTurbo(false);

        Assert.Equal(("quiet", "0,0"), (m.Mode, m.Fans));
        Assert.Equal("quiet", off.To);
    }

    [Fact]
    public void Mode_key_cycles_on_ac_and_resets_the_fans_on_quiet()
    {
        using var m = new FakeNitro("performance", fans: "60,60");
        var c = m.Controller();

        var step = c.PressModeKey(ModeKeyBehavior.Cycle);
        Assert.Equal(("quiet", "0,0"), (m.Mode, m.Fans));
        Assert.Equal("auto", step.Fans);

        m.Tree.File($"{FakeNitro.Ns}/fan_speed", "60,60");
        c.PressModeKey(ModeKeyBehavior.Cycle);
        Assert.Equal(("balanced", "60,60"), (m.Mode, m.Fans));
        c.PressModeKey(ModeKeyBehavior.Cycle);
        Assert.Equal("performance", m.Mode);
    }

    [Fact]
    public void Mode_key_on_battery_and_in_turbo_toggle_mode()
    {
        using var m = new FakeNitro("balanced", onAc: false);
        var c = m.Controller();
        c.PressModeKey(ModeKeyBehavior.TurboToggle);
        Assert.Equal("low-power", m.Mode); // battery: eco <-> balanced, whatever the behavior

        m.SetPower(onAc: true);
        Assert.True(c.PressModeKey(ModeKeyBehavior.TurboToggle).Turbo);
        Assert.Equal(("performance", "100,100"), (m.Mode, m.Fans));
        Assert.False(c.PressModeKey(ModeKeyBehavior.TurboToggle).Turbo);
        Assert.Equal("low-power", m.Mode);
    }

    [Fact]
    public void Gpu_overclock_can_be_disabled_and_a_suspended_gpu_is_left_alone()
    {
        using var m = new FakeNitro("performance");
        var c = m.Controller();
        Assert.Equal(GpuApplyStatus.Applied, c.ApplyGpuPolicy().Status);
        Assert.Equal(GpuApplyStatus.Unchanged, c.ApplyGpuPolicy().Status);

        c.SetGpuOverclockEnabled(false);
        Assert.Equal(GpuClockOffsets.None, m.Gpu0.Offsets);
        c.SetGpuOverclockEnabled(true);
        Assert.Equal(Extreme, m.Gpu0.Offsets);

        m.Gpu0.IsAwake = false;
        var writes = m.Gpu0.Writes;
        c.SetMode("balanced");
        Assert.Equal(GpuApplyStatus.Suspended, c.ApplyGpuPolicy().Status);
        Assert.Equal(writes, m.Gpu0.Writes);
        Assert.Null(c.ReadGpuOffsets());
    }

    [Fact]
    public void User_gpu_levels_replace_the_profile_levels_until_reset()
    {
        using var m = new FakeNitro("performance");
        var c = m.Controller();
        GpuClockOffsets[] mine = [new(0, 0), new(50, 100), new(150, 400)];

        Assert.Equal(GpuApplyStatus.Applied, c.SetGpuLevels(mine).Status);
        Assert.Equal(mine[2], m.Gpu0.Offsets);
        Assert.Equal(mine, c.EffectiveGpuEntry()!.Levels);
        c.SetMode("balanced");
        Assert.Equal(mine[1], m.Gpu0.Offsets);

        // Survives a new controller (daemon restart)
        Assert.Equal(mine[1], m.Controller().DesiredGpuOffsets());

        Assert.Throws<NitroException>(() => c.SetGpuLevels([new(0, 0), new(0, 0)]));
        Assert.Throws<NitroException>(() => c.SetGpuLevels([new(0, 0), new(0, 0), new(500, 0)]));
        Assert.Equal(mine[1], m.Gpu0.Offsets);

        c.SetGpuLevels(null);
        Assert.Null(c.State.GpuLevels);
        Assert.Equal(GpuClockOffsets.None, m.Gpu0.Offsets);
        c.SetMode("performance");
        Assert.Equal(Extreme, m.Gpu0.Offsets);

        // Setting the profile's own levels stores nothing
        c.SetGpuLevels(c.Machine.GpuOcEntry!.Levels.ToList());
        Assert.Null(c.State.GpuLevels);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void A_refused_hardware_write_leaves_the_state_as_it_was()
    {
        using var m = new FakeNitro("balanced");
        var c = m.Controller();
        var profilePath = Path.Join(m.Tree.Root, "sys/firmware/acpi/platform_profile");
        File.SetUnixFileMode(profilePath, UnixFileMode.UserRead);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => c.SetTurbo(true));
            Assert.Null(c.State.Turbo);
        }
        finally
        {
            File.SetUnixFileMode(profilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Features_write_the_driver_attributes()
    {
        using var m = new FakeNitro();
        var c = m.Controller();

        c.SetFeature(NitroSenseDevice.BatteryLimiter, true);
        c.SetFeature(NitroSenseDevice.BootSound, false);
        c.SetUsbCharging(20);
        c.SetFans(new FanSpeeds(45, 55));

        Assert.Equal(("1", "0", "20", "45,55"),
            (m.Attr("battery_limiter"), m.Attr("boot_animation_sound"), m.Attr("usb_charging"), m.Fans));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetUsbCharging(25));

        File.Delete(Path.Join(m.Tree.Root, FakeNitro.Ns.TrimStart('/'), "lcd_override"));
        Assert.Throws<NitroException>(() => c.SetFeature(NitroSenseDevice.LcdOverdrive, true));
    }

    [Fact]
    public void Keyboard_needs_four_zoned_kb()
    {
        using (var m = new FakeNitro())
        {
            var c = m.Controller();
            Assert.Null(c.Keyboard);
            Assert.Throws<NitroException>(() => c.SetKeyboardZones(PerZoneColors.Uniform(new Rgb(1, 2, 3), 50)));
        }

        using (var m = new FakeNitro().WithKeyboard())
        {
            var c = m.Controller();
            c.SetKeyboardEffect(new KeyboardEffectState(KeyboardEffect.Breathing, 2, 70, KeyboardDirection.None, new Rgb(0, 255, 0)));
            Assert.Equal("1,2,70,0,0,255,0", m.Tree.Read(FourZoneKeyboard.EffectPath));
        }
    }

    [Fact]
    public void State_store_survives_missing_and_corrupt_files()
    {
        using var tree = new TestTree();
        var path = Path.Join(tree.Root, "sub", "state.json");
        var store = new StateStore(path);
        Assert.Equal(new NitroState(), store.Load());

        var state = new NitroState { LastNonTurboMode = "quiet", Turbo = new TurboSnapshot("quiet", "0,0"), GpuOverclockDisabled = true };
        store.Save(state);
        Assert.Equal(state, store.Load());

        File.WriteAllText(path, "{ broken");
        Assert.Equal(new NitroState(), store.Load());
        File.WriteAllText(path, """{"schemaVersion": 99, "lastNonTurboMode": "quiet"}""");
        Assert.Equal(new NitroState(), store.Load());
    }
}
