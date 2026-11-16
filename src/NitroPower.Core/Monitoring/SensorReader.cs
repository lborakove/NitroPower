using NitroPower.Core.Hardware;
using NitroPower.Core.IO;
using NitroPower.Core.Nvidia;

namespace NitroPower.Core.Monitoring;

public sealed record SensorSnapshot(
    DateTimeOffset Time,
    string? PlatformProfile,
    PowerState Power,
    double? CpuTempC,
    double? GpuTempC,
    double? SystemTempC,
    double? SsdTempC,
    int? CpuFanRpm,
    int? GpuFanRpm,
    double? CpuUsagePercent,
    int? CpuFreqAvgMHz,
    int? CpuFreqMaxMHz,
    bool GpuAwake,
    GpuTelemetry? Gpu);

/// <summary>
/// Collects one snapshot of every sensor. Temperatures come from the Acer EC
/// (hwmon "acer": temp1 CPU, temp2 GPU, temp3 system; fan1 CPU, fan2 GPU),
/// falling back to coretemp and NVML. Keep one instance alive: CPU usage is a
/// delta between calls.
/// </summary>
public sealed class SensorReader(ISysfs fs, Func<GpuTelemetry?>? gpuTelemetry = null)
{
    private readonly Hwmon _hwmon = new(fs);
    private readonly CpuUsageSampler _cpu = new(fs);
    private readonly PlatformProfile _profile = new(fs);

    public SensorSnapshot Read()
    {
        var acer = _hwmon.Find("acer");
        var coretemp = _hwmon.Find("coretemp");
        var nvme = _hwmon.Find("nvme");

        var gpuAwake = GpuRuntime.IsAwake(fs);
        GpuTelemetry? gpu = null;
        if (gpuAwake && gpuTelemetry is not null)
        {
            try { gpu = gpuTelemetry(); }
            catch (NvmlException) { }
        }

        var freq = CpuFrequency.Read(fs);
        return new SensorSnapshot(
            Time: DateTimeOffset.Now,
            PlatformProfile: _profile.Current,
            Power: PowerSupplies.Read(fs),
            CpuTempC: _hwmon.Temperature(acer, 1) ?? _hwmon.TemperatureByLabel(coretemp, "Package id 0"),
            GpuTempC: _hwmon.Temperature(acer, 2) ?? gpu?.TemperatureC,
            SystemTempC: _hwmon.Temperature(acer, 3),
            SsdTempC: _hwmon.TemperatureByLabel(nvme, "Composite"),
            CpuFanRpm: _hwmon.FanRpm(acer, 1),
            GpuFanRpm: _hwmon.FanRpm(acer, 2),
            CpuUsagePercent: _cpu.Sample(),
            CpuFreqAvgMHz: freq?.Average,
            CpuFreqMaxMHz: freq?.Max,
            GpuAwake: gpuAwake,
            Gpu: gpu);
    }
}
