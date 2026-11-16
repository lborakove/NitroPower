using NitroPower.Core.IO;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Nvidia;

/// <summary>GPU clock offsets, behind an interface so control logic can be tested.</summary>
public interface IGpuControl
{
    /// <summary>False while runtime-suspended; reading offsets would wake it.</summary>
    bool IsAwake { get; }

    GpuClockOffsets GetOffsets();

    /// <summary>Needs root.</summary>
    void SetOffsets(GpuClockOffsets offsets);
}

/// <summary><see cref="IGpuControl"/> over NVML, one session per call.</summary>
public sealed class NvmlGpuControl(ISysfs fs) : IGpuControl
{
    public bool IsAwake => GpuRuntime.IsAwake(fs);

    public GpuClockOffsets GetOffsets()
    {
        using var session = NvmlSession.Open();
        return session.GetClockOffsets();
    }

    public void SetOffsets(GpuClockOffsets offsets)
    {
        using var session = NvmlSession.Open();
        session.SetClockOffsets(offsets);
    }
}
