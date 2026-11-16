using System.Runtime.InteropServices;
using NitroPower.Core.IO;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Nvidia;

public sealed class NvmlException(string operation, int code, string message)
    : Exception($"{operation}: {message} (NVML {code})")
{
    public int Code { get; } = code;
}

public sealed record GpuTelemetry(
    int? TemperatureC,
    double? PowerW,
    double? PowerLimitW,
    int? CoreClockMHz,
    int? MemClockMHz,
    int? UtilizationPercent);

/// <summary>
/// One NVML session bound to a GPU. NVML wakes a runtime-suspended GPU, so
/// callers check <see cref="GpuRuntime.IsAwake"/> first when that matters.
/// Clock offsets need root.
/// </summary>
public sealed class NvmlSession : IDisposable
{
    private readonly IntPtr _device;
    private bool _disposed;

    private NvmlSession(IntPtr device) => _device = device;

    public static NvmlSession Open(uint index = 0)
    {
        try
        {
            Check(NvmlNative.Init(), "nvmlInit");
        }
        catch (DllNotFoundException)
        {
            throw new NvmlException("nvmlInit", -1, "libnvidia-ml.so.1 not found (NVIDIA driver missing)");
        }
        var rc = NvmlNative.DeviceGetHandleByIndex(index, out var device);
        if (rc != NvmlNative.Success)
        {
            NvmlNative.Shutdown();
            Check(rc, "nvmlDeviceGetHandleByIndex");
        }
        return new NvmlSession(device);
    }

    /// <summary>Current offsets in NitroSense units (memory halved from NVML).</summary>
    public GpuClockOffsets GetClockOffsets()
    {
        Check(NvmlNative.DeviceGetGpcClkVfOffset(_device, out var core), "nvmlDeviceGetGpcClkVfOffset");
        Check(NvmlNative.DeviceGetMemClkVfOffset(_device, out var mem), "nvmlDeviceGetMemClkVfOffset");
        return new GpuClockOffsets(core, mem / 2);
    }

    /// <summary>Applies P0 offsets, the NVML counterpart of NvAPI_GPU_SetPstates20.</summary>
    public void SetClockOffsets(GpuClockOffsets offsets)
    {
        try
        {
            SetPstateOffset(NvmlNative.ClockGraphics, offsets.Core);
            SetPstateOffset(NvmlNative.ClockMem, offsets.MemNvml);
        }
        catch (EntryPointNotFoundException)
        {
            // Drivers older than 555: same offsets through the VF curve API
            Check(NvmlNative.DeviceSetGpcClkVfOffset(_device, offsets.Core), "nvmlDeviceSetGpcClkVfOffset");
            Check(NvmlNative.DeviceSetMemClkVfOffset(_device, offsets.MemNvml), "nvmlDeviceSetMemClkVfOffset");
        }
    }

    private void SetPstateOffset(int clockType, int offsetMHz)
    {
        var info = new NvmlNative.ClockOffset
        {
            Version = NvmlNative.ClockOffset.V1,
            Type = clockType,
            Pstate = NvmlNative.Pstate0,
            ClockOffsetMHz = offsetMHz,
        };
        Check(NvmlNative.DeviceSetClockOffsets(_device, ref info), "nvmlDeviceSetClockOffsets");
    }

    public GpuTelemetry GetTelemetry()
    {
        int? temp = NvmlNative.DeviceGetTemperature(_device, NvmlNative.TemperatureGpu, out var t) == 0 ? (int)t : null;
        double? power = NvmlNative.DeviceGetPowerUsage(_device, out var mw) == 0 ? mw / 1000.0 : null;
        double? limit = NvmlNative.DeviceGetEnforcedPowerLimit(_device, out var lmw) == 0 ? lmw / 1000.0 : null;
        int? core = NvmlNative.DeviceGetClockInfo(_device, NvmlNative.ClockGraphics, out var c) == 0 ? (int)c : null;
        int? mem = NvmlNative.DeviceGetClockInfo(_device, NvmlNative.ClockMem, out var m) == 0 ? (int)m : null;
        int? util = NvmlNative.DeviceGetUtilizationRates(_device, out var u) == 0 ? (int)u.Gpu : null;
        return new GpuTelemetry(temp, power, limit, core, mem, util);
    }

    private static void Check(int rc, string operation)
    {
        if (rc == NvmlNative.Success)
            return;
        var message = Marshal.PtrToStringUTF8(NvmlNative.ErrorString(rc)) ?? "unknown error";
        throw new NvmlException(operation, rc, message);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NvmlNative.Shutdown();
    }
}

/// <summary>Runtime power state of the NVIDIA GPU, read without waking it.</summary>
public static class GpuRuntime
{
    public const string DriverPath = "/sys/bus/pci/drivers/nvidia";

    /// <summary>False when the GPU is runtime-suspended (an NVML call would wake
    /// it), or when the nvidia driver is not bound.</summary>
    public static bool IsAwake(ISysfs fs)
    {
        var devices = fs.ListDirectories(DriverPath)
            .Where(d => Path.GetFileName(d).StartsWith("0000:", StringComparison.Ordinal))
            .ToList();
        return devices.Count > 0 &&
               devices.All(d => (fs.ReadText($"{d}/power/runtime_status") ?? "active") == "active");
    }
}
