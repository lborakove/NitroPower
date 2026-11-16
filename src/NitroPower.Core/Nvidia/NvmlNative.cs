using System.Runtime.InteropServices;

namespace NitroPower.Core.Nvidia;

/// <summary>P/Invoke surface of libnvidia-ml.so.1 (ships with the NVIDIA driver).</summary>
internal static partial class NvmlNative
{
    private const string Lib = "libnvidia-ml.so.1";

    public const int Success = 0;
    public const int ClockGraphics = 0;
    public const int ClockMem = 2;
    public const int TemperatureGpu = 0;
    public const int Pstate0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct ClockOffset
    {
        public uint Version;
        public int Type;
        public int Pstate;
        public int ClockOffsetMHz;
        public int MinClockOffsetMHz;
        public int MaxClockOffsetMHz;

        /// <summary>NVML struct version tag: sizeof | (1 &lt;&lt; 24).</summary>
        public static uint V1 => (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [LibraryImport(Lib, EntryPoint = "nvmlInit_v2")]
    public static partial int Init();

    [LibraryImport(Lib, EntryPoint = "nvmlShutdown")]
    public static partial int Shutdown();

    [LibraryImport(Lib, EntryPoint = "nvmlErrorString")]
    public static partial IntPtr ErrorString(int result);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static partial int DeviceGetHandleByIndex(uint index, out IntPtr device);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetGpcClkVfOffset")]
    public static partial int DeviceGetGpcClkVfOffset(IntPtr device, out int offset);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetMemClkVfOffset")]
    public static partial int DeviceGetMemClkVfOffset(IntPtr device, out int offset);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceSetClockOffsets")]
    public static partial int DeviceSetClockOffsets(IntPtr device, ref ClockOffset info);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceSetGpcClkVfOffset")]
    public static partial int DeviceSetGpcClkVfOffset(IntPtr device, int offset);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceSetMemClkVfOffset")]
    public static partial int DeviceSetMemClkVfOffset(IntPtr device, int offset);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")]
    public static partial int DeviceGetTemperature(IntPtr device, int sensor, out uint celsius);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static partial int DeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    public static partial int DeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static partial int DeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static partial int DeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
}
