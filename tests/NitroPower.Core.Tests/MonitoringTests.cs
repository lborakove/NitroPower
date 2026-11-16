using NitroPower.Core.Monitoring;
using NitroPower.Core.Nvidia;

namespace NitroPower.Core.Tests;

public class MonitoringTests
{
    [Fact]
    public void Cpu_stat_parse_counts_idle_and_iowait_as_idle()
    {
        var parsed = CpuUsageSampler.Parse("cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 1 2 3 4 5");
        Assert.Equal((150UL, 1000UL), parsed);
        Assert.Null(CpuUsageSampler.Parse("garbage"));
    }

    [Fact]
    public void Cpu_usage_is_a_delta_between_samples()
    {
        using var tree = new TestTree().File("/proc/stat", "cpu  100 0 0 900 0 0 0 0");
        var sampler = new CpuUsageSampler(tree.Fs);
        Assert.Null(sampler.Sample());
        tree.File("/proc/stat", "cpu  150 0 0 950 0 0 0 0"); // +50 busy of +100
        Assert.Equal(50.0, sampler.Sample());
    }

    [Fact]
    public void Sensor_reader_uses_the_acer_ec_sensors()
    {
        using var tree = new TestTree()
            .File("/sys/class/hwmon/hwmon5/name", "acer")
            .File("/sys/class/hwmon/hwmon5/temp1_input", "61000")
            .File("/sys/class/hwmon/hwmon5/temp2_input", "66000")
            .File("/sys/class/hwmon/hwmon5/temp3_input", "58000")
            .File("/sys/class/hwmon/hwmon5/fan1_input", "4878")
            .File("/sys/class/hwmon/hwmon5/fan2_input", "4739")
            .File("/sys/class/hwmon/hwmon3/name", "nvme")
            .File("/sys/class/hwmon/hwmon3/temp1_input", "30850")
            .File("/sys/class/hwmon/hwmon3/temp1_label", "Composite")
            .File("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "2400000")
            .File("/sys/devices/system/cpu/cpu1/cpufreq/scaling_cur_freq", "400000")
            .File("/sys/firmware/acpi/platform_profile", "performance")
            .File("/proc/stat", "cpu  1 0 0 1 0 0 0 0");
        var gpuCalls = 0;
        var reader = new SensorReader(tree.Fs, () => { gpuCalls++; return null; });

        var s = reader.Read();

        Assert.Equal(61.0, s.CpuTempC);
        Assert.Equal(66.0, s.GpuTempC);
        Assert.Equal(58.0, s.SystemTempC);
        Assert.Equal(30.85, s.SsdTempC);
        Assert.Equal(4878, s.CpuFanRpm);
        Assert.Equal(4739, s.GpuFanRpm);
        Assert.Equal(1400, s.CpuFreqAvgMHz);
        Assert.Equal(2400, s.CpuFreqMaxMHz);
        Assert.Equal("performance", s.PlatformProfile);
        Assert.False(s.GpuAwake);   // no nvidia driver in the fake tree
        Assert.Equal(0, gpuCalls);  // so NVML must not be touched (it would wake the GPU)
    }

    [Fact]
    public void Gpu_is_awake_only_when_every_nvidia_device_is_active()
    {
        using var tree = new TestTree()
            .File($"{GpuRuntime.DriverPath}/0000:01:00.0/power/runtime_status", "active");
        Assert.True(GpuRuntime.IsAwake(tree.Fs));
        tree.File($"{GpuRuntime.DriverPath}/0000:01:00.0/power/runtime_status", "suspended");
        Assert.False(GpuRuntime.IsAwake(tree.Fs));
    }
    [Fact]
    public void Attribute_watcher_reads_values_and_times_out_without_notifications()
    {
        using var tree = new TestTree().File("/attr", "performance");
        var path = Path.Join(tree.Root, "attr");
        using var watcher = new AttributeWatcher([path, Path.Join(tree.Root, "missing")]);

        Assert.Equal([path], watcher.Paths);
        Assert.Equal("performance", watcher.ReadAll().Single().Value);
        Assert.Empty(watcher.Wait(50)); // regular files never raise POLLPRI
    }
}
