using NitroPower.Core.Hardware;
using NitroPower.Core.IO;
using NitroPower.Core.Profiles;

namespace NitroPower.Core;

/// <summary>What this machine is and which profile and GPU entry apply to it.</summary>
public sealed record MachineInfo(
    DmiInfo Dmi,
    ModelProfile? Profile,
    PciDevice? NvidiaGpu,
    GpuOcDevice? GpuOcEntry,
    NitroSenseDevice? NitroSense,
    bool PlatformProfileAvailable)
{
    public static MachineInfo Detect(ISysfs fs, ProfileCatalog catalog)
    {
        var dmi = DmiInfo.Read(fs);
        var profile = catalog.Match(dmi);
        var gpu = PciDevices.NvidiaGpus(PciDevices.Read(fs)).FirstOrDefault();
        var ocEntry = gpu is null ? null : profile?.GpuOverclock?.FindDevice(gpu.PciId, gpu.Subsystem);
        return new MachineInfo(
            dmi,
            profile,
            gpu,
            ocEntry,
            NitroSenseDevice.Detect(fs),
            new PlatformProfile(fs).IsAvailable);
    }

    /// <summary>Default profile directories, plus "profiles" next to the
    /// executable (portable builds and development runs).</summary>
    public static IEnumerable<string> ProfileDirectories(string? extra = null)
    {
        foreach (var dir in ProfileCatalog.DefaultDirectories)
            yield return dir;
        yield return Path.Join(AppContext.BaseDirectory, "profiles");
        if (extra is not null)
            yield return extra;
    }
}
