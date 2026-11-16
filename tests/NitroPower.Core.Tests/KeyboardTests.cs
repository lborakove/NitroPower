using NitroPower.Core.Hardware;

namespace NitroPower.Core.Tests;

public class KeyboardTests
{
    [Theory]
    [InlineData("ff8000", 255, 128, 0)]
    [InlineData("#4287F5", 0x42, 0x87, 0xf5)]
    public void Rgb_parses_hex(string text, int r, int g, int b)
    {
        var c = Rgb.TryParseHex(text);
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), c);
        Assert.Equal(text.TrimStart('#').ToLowerInvariant(), c!.Value.ToHex());
    }

    [Theory]
    [InlineData("fff")]
    [InlineData("gggggg")]
    [InlineData("1234567")]
    [InlineData(null)]
    public void Rgb_rejects_bad_hex(string? text) => Assert.Null(Rgb.TryParseHex(text));

    [Fact]
    public void Normalize_zeroes_what_the_effect_ignores_like_the_driver()
    {
        var red = new Rgb(255, 0, 0);

        var still = new KeyboardEffectState(KeyboardEffect.Static, 5, 80, KeyboardDirection.LeftToRight, red).Normalize();
        Assert.Equal((0, KeyboardDirection.None, red), (still.Speed, still.Direction, still.Color));

        var neon = new KeyboardEffectState(KeyboardEffect.Neon, 3, 80, KeyboardDirection.RightToLeft, red).Normalize();
        Assert.Equal((3, KeyboardDirection.None, Rgb.Black), (neon.Speed, neon.Direction, neon.Color));

        var wave = new KeyboardEffectState(KeyboardEffect.Wave, 3, 80, KeyboardDirection.LeftToRight, red).Normalize();
        Assert.Equal((KeyboardDirection.LeftToRight, Rgb.Black), (wave.Direction, wave.Color));

        var meteor = new KeyboardEffectState(KeyboardEffect.Meteor, 3, 80, KeyboardDirection.LeftToRight, red).Normalize();
        Assert.Equal((KeyboardDirection.None, red), (meteor.Direction, meteor.Color));
    }

    [Fact]
    public void Validate_rejects_what_the_driver_rejects()
    {
        var white = new Rgb(255, 255, 255);
        Assert.Throws<ArgumentException>(() =>
            new KeyboardEffectState(KeyboardEffect.Wave, 3, 100, KeyboardDirection.None, white).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KeyboardEffectState(KeyboardEffect.Breathing, 10, 100, KeyboardDirection.None, white).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KeyboardEffectState(KeyboardEffect.Static, 0, 101, KeyboardDirection.None, white).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KeyboardEffectState((KeyboardEffect)8, 0, 50, KeyboardDirection.None, white).Validate());
    }

    [Fact]
    public void Effect_round_trips_through_the_driver_format()
    {
        var e = new KeyboardEffectState(KeyboardEffect.Shifting, 4, 90, KeyboardDirection.RightToLeft, new Rgb(1, 2, 3));
        Assert.Equal("4,4,90,1,1,2,3", e.ToSysfs());
        Assert.Equal(e, KeyboardEffectState.TryParse(e.ToSysfs()));
    }

    [Theory]
    [InlineData("255,0,0,0,0,0,0")]   // firmware without the keyboard engine
    [InlineData("1,2,3")]
    [InlineData("")]
    [InlineData(null)]
    public void Effect_parse_rejects_junk(string? text) => Assert.Null(KeyboardEffectState.TryParse(text));

    [Fact]
    public void Zones_round_trip_and_validate()
    {
        const string text = "4287f5,ff5733,33ff57,ff33a6,100";
        var z = PerZoneColors.TryParse(text);
        Assert.NotNull(z);
        Assert.Equal(new Rgb(0xff, 0x57, 0x33), z.Zone2);
        Assert.Equal(text, z.ToSysfs());

        Assert.Null(PerZoneColors.TryParse("4287f5,ff5733,33ff57,100"));
        Assert.Null(PerZoneColors.TryParse("4287f5,ff5733,33ff57,ff33a6,101"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PerZoneColors.Uniform(new Rgb(1, 1, 1), 150).Validate());
    }

    [Fact]
    public void Keyboard_device_writes_normalized_values()
    {
        using var tree = new TestTree()
            .File(FourZoneKeyboard.EffectPath, "0,0,100,0,255,255,255")
            .File(FourZoneKeyboard.ZonesPath, "ffffff,ffffff,ffffff,ffffff,100");
        var kb = FourZoneKeyboard.Detect(tree.Fs);
        Assert.NotNull(kb);
        Assert.Equal(KeyboardEffect.Static, kb.GetEffect()!.Effect);

        kb.SetEffect(new KeyboardEffectState(KeyboardEffect.Wave, 5, 80, KeyboardDirection.LeftToRight, new Rgb(9, 9, 9)));
        Assert.Equal("3,5,80,2,0,0,0", tree.Read(FourZoneKeyboard.EffectPath));

        kb.SetZones(PerZoneColors.Uniform(new Rgb(0x42, 0x87, 0xf5), 60));
        Assert.Equal("4287f5,4287f5,4287f5,4287f5,60", tree.Read(FourZoneKeyboard.ZonesPath));

        using var empty = new TestTree();
        Assert.Null(FourZoneKeyboard.Detect(empty.Fs));
    }
}
