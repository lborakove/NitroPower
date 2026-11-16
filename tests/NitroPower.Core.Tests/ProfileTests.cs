using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

public class ProfileTests
{
    private static ProfileCatalog RepoCatalog() => ProfileCatalog.Load([TestTree.RepoProfilesDirectory()]);

    [Fact]
    public void Repository_profiles_all_load()
    {
        var catalog = RepoCatalog();
        Assert.Empty(catalog.Errors);
        Assert.Contains(catalog.Profiles, p => p.Id == "acer-nitro-anv15-52");
    }

    [Fact]
    public void Anv15_52_profile_has_the_measured_values()
    {
        var p = RepoCatalog().Profiles.Single(p => p.Id == "acer-nitro-anv15-52");

        Assert.False(p.ThermalModes.TurboSupported);
        Assert.Equal(["eco", "quiet", "balanced", "performance"], p.ThermalModes.Modes.Select(m => m.Id));
        Assert.Equal(4, p.FindModeByPlatformProfile("performance")!.FirmwareValue);
        Assert.Equal(6, p.FindMode("eco")!.FirmwareValue);

        var gpu = p.GpuOverclock!.FindDevice("10de:28a0", "1025:171e");
        Assert.NotNull(gpu);
        Assert.Equal(new GpuClockOffsets(100, 200), gpu.Levels[p.FindMode("performance")!.GpuOcLevel]);
        Assert.Null(p.GpuOverclock.FindDevice("10de:28a0", "1025:1854"));

        Assert.Equal("battery_limiter", p.FeatureAttribute("batteryLimiter"));
        Assert.Null(p.FeatureAttribute("bootLogo")); // "unknown"
        Assert.Equal(148, p.NitroKey!.EvdevCode);

        Assert.Equal(["quiet", "balanced", "performance"], p.EffectiveModeCycle.Ac);
        Assert.Equal(["eco", "balanced"], p.EffectiveModeCycle.Battery);
        Assert.True(p.EffectiveModeCycle.ResetsFans("eco"));
        Assert.Equal(new TurboConfig("performance", "max"), p.EffectiveTurbo);
    }

    private const string Modes = """
        "thermalModes":{"source":"s","turboSupported":false,"modes":[
          {"id":"quiet","firmwareValue":0,"platformProfile":"quiet","gpuOcLevel":0},
          {"id":"balanced","firmwareValue":1,"platformProfile":"balanced","gpuOcLevel":0}]}
        """;

    [Theory]
    [InlineData("""{"modeCycle":{"ac":["quiet","warp"],"battery":["balanced"]}}""", "warp")]
    [InlineData("""{"modeCycle":{"ac":[],"battery":["balanced"]}}""", "at least one")]
    [InlineData("""{"turbo":{"mode":"warp","fans":"max"}}""", "turbo.mode")]
    [InlineData("""{"turbo":{"mode":"balanced","fans":"loud"}}""", "turbo.fans")]
    public void Rejects_bad_mode_cycle_and_turbo(string extra, string expectedMessagePart)
    {
        var json = """{"schemaVersion":1,"id":"x","displayName":"x","match":{"dmi":{"a":"b"}},""" + Modes + "," +
                   extra.TrimStart('{');
        var e = Assert.Throws<InvalidDataException>(() => ProfileCatalog.Parse(json));
        Assert.Contains(expectedMessagePart, e.Message);
    }

    [Fact]
    public void Matches_by_dmi()
    {
        var catalog = RepoCatalog();
        var dmi = new DmiInfo(new Dictionary<string, string>
        {
            ["sys_vendor"] = "Acer",
            ["product_name"] = "Nitro ANV15-52",
            ["bios_version"] = "V1.60",
        });
        Assert.Equal("acer-nitro-anv15-52", catalog.Match(dmi)?.Id);

        var other = new DmiInfo(new Dictionary<string, string> { ["sys_vendor"] = "Acer", ["product_name"] = "Nitro AN515-58" });
        Assert.Null(catalog.Match(other));
    }

    [Fact]
    public void Reads_dmi_from_sysfs()
    {
        using var tree = new TestTree()
            .File("/sys/class/dmi/id/sys_vendor", "Acer")
            .File("/sys/class/dmi/id/product_name", "Nitro ANV15-52");
        var dmi = DmiInfo.Read(tree.Fs);
        Assert.Equal("Nitro ANV15-52", dmi["product_name"]);
        Assert.Null(dmi["board_name"]);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"id":"x","displayName":"x","match":{"dmi":{"a":"b"}},"thermalModes":{"source":"s","modes":[{"id":"a","firmwareValue":0,"platformProfile":"quiet","gpuOcLevel":0}],"turboSupported":false}}""", "schemaVersion")]
    [InlineData("""{"schemaVersion":1,"id":"x","displayName":"x","match":{"dmi":{}},"thermalModes":{"source":"s","modes":[{"id":"a","firmwareValue":0,"platformProfile":"quiet","gpuOcLevel":0}],"turboSupported":false}}""", "match.dmi")]
    [InlineData("""{"schemaVersion":1,"id":"x","displayName":"x","match":{"dmi":{"a":"b"}},"thermalModes":{"source":"s","modes":[],"turboSupported":false}}""", "modes")]
    public void Rejects_invalid_profiles(string json, string expectedMessagePart)
    {
        var e = Assert.Throws<InvalidDataException>(() => ProfileCatalog.Parse(json));
        Assert.Contains(expectedMessagePart, e.Message);
    }

    [Fact]
    public void Later_directory_overrides_same_id_and_bad_files_are_reported()
    {
        using var a = new TestTree();
        using var b = new TestTree();
        const string template = """{"schemaVersion":1,"id":"m","displayName":"NAME","match":{"dmi":{"product_name":"M"}},"thermalModes":{"source":"s","modes":[{"id":"q","firmwareValue":0,"platformProfile":"quiet","gpuOcLevel":0}],"turboSupported":false}}""";
        File.WriteAllText(Path.Join(a.Root, "m.json"), template.Replace("NAME", "first"));
        File.WriteAllText(Path.Join(b.Root, "m.json"), template.Replace("NAME", "second"));
        File.WriteAllText(Path.Join(b.Root, "broken.json"), "{ not json");

        var catalog = ProfileCatalog.Load([a.Root, b.Root]);

        Assert.Equal("second", catalog.Profiles.Single().DisplayName);
        Assert.Single(catalog.Errors);
    }
}
