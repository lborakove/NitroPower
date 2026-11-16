using NitroPower.Core.Hardware;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

public class ModeCyclerTests
{
    private static ModelProfile Anv15() =>
        ProfileCatalog.Load([TestTree.RepoProfilesDirectory()]).Profiles.Single(p => p.Id == "acer-nitro-anv15-52");

    [Theory]
    [InlineData("quiet", "balanced", false)]
    [InlineData("balanced", "performance", false)]
    [InlineData("performance", "quiet", true)]   // wraps, unlike the driver without firmware turbo
    [InlineData("eco", "quiet", true)]           // eco is not in the AC cycle
    [InlineData(null, "quiet", true)]
    public void Ac_cycle(string? current, string expected, bool fansToAuto) =>
        Assert.Equal(new ModeStep(expected, fansToAuto),
            ModeCycler.Next(Anv15(), current, onAc: true, ModeKeyBehavior.Cycle, null));

    [Theory]
    [InlineData("performance", "eco")]
    [InlineData("eco", "balanced")]
    [InlineData("balanced", "eco")]
    public void Battery_alternates_eco_and_balanced(string current, string expected)
    {
        foreach (var behavior in Enum.GetValues<ModeKeyBehavior>())
            Assert.Equal(expected, ModeCycler.Next(Anv15(), current, onAc: false, behavior, null).ModeId);
    }

    [Theory]
    [InlineData("balanced", null, "performance")]
    [InlineData("performance", "quiet", "quiet")]
    [InlineData("performance", null, "balanced")]
    [InlineData("performance", "performance", "balanced")] // never "back" to turbo itself
    [InlineData("performance", "nonsense", "balanced")]
    public void Turbo_toggle(string current, string? lastNonTurbo, string expected) =>
        Assert.Equal(expected,
            ModeCycler.Next(Anv15(), current, onAc: true, ModeKeyBehavior.TurboToggle, lastNonTurbo).ModeId);

    [Fact]
    public void Defaults_without_mode_cycle_or_turbo_in_the_profile()
    {
        var p = ProfileCatalog.Parse("""
            {"schemaVersion":1,"id":"x","displayName":"x","match":{"dmi":{"a":"b"}},
             "thermalModes":{"source":"platform_profile","turboSupported":true,"modes":[
               {"id":"eco","firmwareValue":6,"platformProfile":"low-power","gpuOcLevel":0,"requiresAc":false},
               {"id":"quiet","firmwareValue":0,"platformProfile":"quiet","gpuOcLevel":0,"requiresAc":true},
               {"id":"balanced","firmwareValue":1,"platformProfile":"balanced","gpuOcLevel":1,"requiresAc":false},
               {"id":"performance","firmwareValue":4,"platformProfile":"balanced-performance","gpuOcLevel":2,"requiresAc":true},
               {"id":"turbo","firmwareValue":5,"platformProfile":"performance","gpuOcLevel":3,"requiresAc":true}]}}
            """);

        Assert.Equal(["quiet", "balanced", "performance", "turbo"], p.EffectiveModeCycle.Ac);
        Assert.Equal(["eco", "balanced"], p.EffectiveModeCycle.Battery);
        Assert.Equal(new TurboConfig("turbo", "max"), p.EffectiveTurbo);
        Assert.Equal("quiet", ModeCycler.Next(p, "turbo", true, ModeKeyBehavior.Cycle, null).ModeId);
        Assert.Equal("turbo", ModeCycler.Next(p, "performance", true, ModeKeyBehavior.Cycle, null).ModeId);
    }

    [Theory]
    [InlineData("max", 100, 100)]
    [InlineData("auto", 0, 0)]
    [InlineData("40,60", 40, 60)]
    public void Fan_settings_parse(string text, int cpu, int gpu) =>
        Assert.Equal(new FanSpeeds(cpu, gpu), FanSetting.Parse(text).Speeds);

    [Fact]
    public void Fan_setting_keep_and_errors()
    {
        Assert.True(FanSetting.Parse("keep").IsKeep);
        Assert.Throws<FormatException>(() => FanSetting.Parse("loud"));
        Assert.Null(FanSetting.TryParse("101,0"));
    }
}
