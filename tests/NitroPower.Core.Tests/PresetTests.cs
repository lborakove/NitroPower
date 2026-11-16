using NitroPower.Core.Control;
using NitroPower.Core.Hardware;

namespace NitroPower.Core.Tests;

public class PresetTests
{
    [Fact]
    public void Capture_save_load_and_apply_round_trip()
    {
        using var source = new FakeNitro("quiet", fans: "35,45").WithKeyboard("3,5,80,2,0,0,0");
        var preset = Presets.Capture(source.Controller());

        Assert.Equal("quiet", preset.Mode);
        Assert.Equal("35,45", preset.Fans);
        Assert.False(preset.Turbo);
        Assert.True(preset.KeyboardTimeout);
        Assert.Equal(0, preset.UsbCharging);
        Assert.Equal("3,5,80,2,0,0,0", preset.KeyboardEffect); // a moving effect is kept as the effect
        Assert.Null(preset.KeyboardZones);

        using var dir = new TestTree();
        var store = new PresetStore(Path.Join(dir.Root, "presets"));
        store.Save("silent-wave", preset);
        Assert.Equal(["silent-wave"], store.List());
        Assert.Equal(preset, store.Load("silent-wave"));

        using var target = new FakeNitro("performance").WithKeyboard();
        var results = Presets.Apply(target.Controller(), store.Load("silent-wave")!);

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(("quiet", "35,45"), (target.Mode, target.Fans));
        Assert.Equal("3,5,80,2,0,0,0", target.Tree.Read(FourZoneKeyboard.EffectPath));
        Assert.True(store.Delete("silent-wave"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Preset_count_is_capped_but_existing_ones_can_be_replaced()
    {
        using var dir = new TestTree();
        var store = new PresetStore(Path.Join(dir.Root, "presets"));
        for (var i = 0; i < PresetStore.MaxPresets; i++)
            store.Save($"p{i}", new Preset { Mode = "quiet" });
        Assert.Throws<NitroException>(() => store.Save("one-more", new Preset()));
        store.Save("p0", new Preset { Mode = "eco" });
        Assert.Equal("eco", store.Load("p0")!.Mode);
    }

    [Fact]
    public void A_static_keyboard_is_saved_as_zones()
    {
        using var m = new FakeNitro().WithKeyboard("0,0,60,0,255,0,0", "ff0000,00ff00,0000ff,ffffff,60");
        var preset = Presets.Capture(m.Controller());
        Assert.Null(preset.KeyboardEffect);
        Assert.Equal("ff0000,00ff00,0000ff,ffffff,60", preset.KeyboardZones);
    }

    [Fact]
    public void Turbo_is_captured_with_what_lies_underneath()
    {
        using var source = new FakeNitro("balanced", fans: "20,20");
        var c = source.Controller();
        c.SetTurbo(true);
        var preset = Presets.Capture(c);
        Assert.Equal(("balanced", "20,20", true), (preset.Mode, preset.Fans, preset.Turbo));

        using var target = new FakeNitro("quiet");
        var t = target.Controller();
        Assert.All(Presets.Apply(t, preset), r => Assert.True(r.Ok, r.Error));
        Assert.True(t.IsTurboActive);
        Assert.Equal(new TurboSnapshot("balanced", "20,20"), t.State.Turbo);
    }

    [Fact]
    public void One_failing_setting_does_not_stop_the_rest()
    {
        using var m = new FakeNitro("balanced");
        var results = Presets.Apply(m.Controller(), new Preset
        {
            BatteryLimiter = true,
            KeyboardEffect = "1,2,70,0,0,255,0", // no RGB keyboard here
            UsbCharging = 10,
        });

        Assert.Single(results, r => !r.Ok && r.Setting == "keyboardEffect");
        Assert.Equal(("1", "10"), (m.Attr("battery_limiter"), m.Attr("usb_charging")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("../etc/passwd")]
    [InlineData("a b")]
    public void Preset_names_cannot_escape_the_directory(string name)
    {
        Assert.False(PresetStore.IsValidName(name));
        using var dir = new TestTree();
        Assert.Throws<NitroException>(() => new PresetStore(dir.Root).Save(name, new Preset()));
    }
}
