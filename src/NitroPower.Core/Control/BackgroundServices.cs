using NitroPower.Core.Hardware;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Control;

/// <summary>
/// Keeps the GPU offsets on what the mode and power state call for (the job
/// nitro-gpu-oc did). Never wakes a suspended GPU, and does not even open NVML
/// while nothing is applied and nothing is wanted, so the GPU can sleep. While
/// overclocked, it reads the offsets back on every call and applies them again
/// if a GPU reset or a resume from suspend dropped them.
/// </summary>
public sealed class GpuOverclockService(NitroController controller, IGpuControl gpu)
{
    private GpuClockOffsets? _applied;

    public GpuClockOffsets? Applied => _applied;

    public GpuApplyResult Sync()
    {
        if (controller.Machine.GpuOcEntry is null)
            return GpuApplyResult.NotApplicable;
        var want = controller.DesiredGpuOffsets();
        if (want.IsZero && _applied is { IsZero: true })
            return new GpuApplyResult(GpuApplyStatus.Unchanged, want);
        if (!gpu.IsAwake)
            return new GpuApplyResult(GpuApplyStatus.Suspended);
        try
        {
            var current = gpu.GetOffsets();
            var status = GpuApplyStatus.Unchanged;
            if (current != want)
            {
                gpu.SetOffsets(want);
                current = gpu.GetOffsets();
                status = GpuApplyStatus.Applied;
            }
            _applied = current;
            return new GpuApplyResult(status, current);
        }
        catch (NvmlException e)
        {
            _applied = null;
            return new GpuApplyResult(GpuApplyStatus.Failed, Detail: e.Message);
        }
    }

    /// <summary>Back to stock clocks (daemon stop).</summary>
    public void Reset()
    {
        if (_applied is null or { IsZero: true } || !gpu.IsAwake)
            return;
        try
        {
            gpu.SetOffsets(GpuClockOffsets.None);
            _applied = GpuClockOffsets.None;
        }
        catch (NvmlException)
        {
        }
    }
}

/// <summary>
/// Runs the software fan curves while the fan choice is "curve": the CPU fan
/// follows the CPU temperature and the GPU fan the GPU temperature, with
/// hysteresis, and the driver is written only when a duty changes. Turbo sets
/// the choice to "max", which pauses the engine until turbo restores "curve".
/// If no temperature can be read, the fans go back to the firmware.
/// </summary>
public sealed class FanCurveEngine(NitroController controller, Func<(double? Cpu, double? Gpu)> temperatures)
{
    private FanCurveSettings? _settings;
    private FanCurveController? _cpu, _gpu;
    private FanSpeeds? _written;

    /// <summary>Speeds last written by the engine (null while idle).</summary>
    public FanSpeeds? Written => _written;

    public bool IsActive { get; private set; }

    /// <summary>One step; returns the speeds written, or null when nothing was written.</summary>
    public FanSpeeds? Tick()
    {
        var state = controller.State;
        if (state.Fans != FanSetting.CurveText || controller.Sense is null)
        {
            IsActive = false;
            _written = null;
            return null;
        }

        var settings = state.EffectiveFanCurve;
        if (!settings.Equals(_settings))
        {
            var (cpuCurve, gpuCurve) = settings.Build();
            _settings = settings;
            _cpu = new FanCurveController(cpuCurve);
            _gpu = new FanCurveController(gpuCurve);
            _written = null;
        }
        IsActive = true;

        var (cpuTemp, gpuTemp) = temperatures();
        FanSpeeds target;
        if (cpuTemp is null && gpuTemp is null)
            target = FanSpeeds.Auto; // blind: let the firmware protect the machine
        else
            target = new FanSpeeds(
                _cpu!.Next(cpuTemp ?? gpuTemp!.Value),
                _gpu!.Next(gpuTemp ?? cpuTemp!.Value));

        if (target == _written)
            return null;
        controller.WriteFanSpeeds(target);
        _written = target;
        return target;
    }
}
