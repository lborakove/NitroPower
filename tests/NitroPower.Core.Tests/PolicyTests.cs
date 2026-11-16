using NitroPower.Core.Hardware;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

public class PolicyTests
{
    private static (ModelProfile Profile, GpuOcDevice Device) Anv15()
    {
        var p = ProfileCatalog.Load([TestTree.RepoProfilesDirectory()]).Profiles.Single(p => p.Id == "acer-nitro-anv15-52");
        return (p, p.GpuOverclock!.FindDevice("10de:28a0", "1025:171e")!);
    }

    private static readonly PowerState Ac = new(true, 100);

    [Theory]
    [InlineData("performance", 100, 200)]
    [InlineData("balanced", 0, 0)]
    [InlineData("quiet", 0, 0)]
    [InlineData("low-power", 0, 0)]
    [InlineData("custom-unknown", 0, 0)]
    [InlineData(null, 0, 0)]
    public void Mode_selects_the_nitrosense_oc_level(string? platformProfile, int core, int mem)
    {
        var (profile, device) = Anv15();
        var policy = new GpuOverclockPolicy(profile, device);
        Assert.Equal(new GpuClockOffsets(core, mem), policy.Decide(platformProfile, Ac));
    }

    [Fact]
    public void Overclock_needs_ac_power()
    {
        var (profile, device) = Anv15();
        var policy = new GpuOverclockPolicy(profile, device);
        Assert.True(policy.Decide("performance", new PowerState(false, 90)).IsZero);
    }

    [Fact]
    public void Battery_hysteresis_stops_below_30_and_resumes_at_40()
    {
        var (profile, device) = Anv15();
        var policy = new GpuOverclockPolicy(profile, device);

        Assert.False(policy.Decide("performance", new PowerState(true, 35)).IsZero);
        Assert.True(policy.Decide("performance", new PowerState(true, 29)).IsZero);
        Assert.True(policy.Decide("performance", new PowerState(true, 39)).IsZero); // still recovering
        Assert.False(policy.Decide("performance", new PowerState(true, 40)).IsZero);
    }

    [Fact]
    public void Disabled_policy_or_unknown_gpu_gives_zero()
    {
        var (profile, device) = Anv15();
        var off = new GpuOverclockPolicy(profile, device) { Enabled = false };
        Assert.True(off.Decide("performance", Ac).IsZero);

        var noGpu = new GpuOverclockPolicy(profile, null);
        Assert.True(noGpu.Decide("performance", Ac).IsZero);
    }

    [Fact]
    public void Memory_offset_is_doubled_for_nvml() =>
        Assert.Equal(400, new GpuClockOffsets(100, 200).MemNvml);

    [Theory]
    [InlineData(30, 20)]  // below the first point
    [InlineData(40, 20)]
    [InlineData(60, 38)]  // halfway between 55→30 and 65→45 = 37.5
    [InlineData(92, 100)]
    [InlineData(105, 100)] // above the last point
    public void Fan_curve_interpolates(double temp, int expected) =>
        Assert.Equal(expected, FanCurve.Default.Evaluate(temp));

    [Fact]
    public void Fan_curve_rejects_unsafe_shapes()
    {
        Assert.Throws<ArgumentException>(() => new FanCurve([new(50, 40)]));
        Assert.Throws<ArgumentException>(() => new FanCurve([new(50, 40), new(60, 0)]));   // 0 = auto
        Assert.Throws<ArgumentException>(() => new FanCurve([new(50, 60), new(60, 40)]));  // decreasing
        Assert.Throws<ArgumentException>(() => new FanCurve([new(50, 40), new(50, 60)]));  // same temp
    }

    [Fact]
    public void Fan_controller_drops_only_after_hysteresis()
    {
        var c = new FanCurveController(new FanCurve([new(50, 30), new(70, 70)]), hysteresisC: 3);

        Assert.Equal(50, c.Next(60));
        Assert.Equal(70, c.Next(70));
        Assert.Equal(70, c.Next(68)); // within 3 °C of 70: hold
        Assert.Equal(64, c.Next(67)); // 3 °C below: follow the curve down
    }
}
