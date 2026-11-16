using System.Text.Json;
using System.Text.Json.Serialization;
using NitroPower.Core.Policy;

namespace NitroPower.Core.Control;

/// <summary>What the Nitro key does (the daemon listens to it).</summary>
public enum NitroKeyAction
{
    /// <summary>Turbo on/off (default).</summary>
    Turbo,

    /// <summary>Next mode, like the linuwu_sense mode key.</summary>
    Cycle,

    /// <summary>Only tell the clients (the GUI opens itself).</summary>
    Gui,

    None,
}

public static class NitroKeyActions
{
    public static string ToText(NitroKeyAction a) => a.ToString().ToLowerInvariant();

    public static NitroKeyAction? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "turbo" => NitroKeyAction.Turbo,
        "cycle" or "mode" => NitroKeyAction.Cycle,
        "gui" => NitroKeyAction.Gui,
        "none" or "off" => NitroKeyAction.None,
        _ => null,
    };
}

/// <summary>The software fan curves (CPU fan follows the CPU, GPU fan the GPU).</summary>
public sealed record FanCurveSettings(IReadOnlyList<FanCurvePoint> Cpu, IReadOnlyList<FanCurvePoint> Gpu)
{
    public static FanCurveSettings Default { get; } = new(FanCurve.Default.Points, FanCurve.Default.Points);

    /// <summary>Throws <see cref="ArgumentException"/> when a curve is unsafe.</summary>
    public (FanCurve Cpu, FanCurve Gpu) Build() => (new FanCurve(Cpu), new FanCurve(Gpu));

    public bool Equals(FanCurveSettings? other) =>
        other is not null && Cpu.SequenceEqual(other.Cpu) && Gpu.SequenceEqual(other.Gpu);

    public override int GetHashCode() => HashCode.Combine(Cpu.Count, Gpu.Count, Cpu.FirstOrDefault(), Gpu.FirstOrDefault());
}

/// <summary>What was in place when turbo started, restored when it ends.</summary>
/// <param name="PreviousMode">Thermal mode id.</param>
/// <param name="PreviousFans">Fan choice to restore: "auto", "max", "cpu,gpu" or "curve".</param>
public sealed record TurboSnapshot(string PreviousMode, string PreviousFans);

/// <summary>NitroPower's own state, kept across runs and reboots.</summary>
public sealed record NitroState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Last mode chosen other than the turbo mode; turbo returns to it.</summary>
    public string? LastNonTurboMode { get; init; }

    /// <summary>Set while turbo is on.</summary>
    public TurboSnapshot? Turbo { get; init; }

    /// <summary>The user switched the GPU overclock off.</summary>
    public bool GpuOverclockDisabled { get; init; }

    /// <summary>The user's GPU offsets per overclock level, replacing the
    /// profile's (same count). Null: the profile's.</summary>
    public IReadOnlyList<Profiles.GpuClockOffsets>? GpuLevels { get; init; }

    /// <summary>Fan choice: "auto", "max", "cpu,gpu" or "curve" (null: never set).</summary>
    public string? Fans { get; init; }

    /// <summary>Curves used when <see cref="Fans"/> is "curve" (null: the default curve).</summary>
    public FanCurveSettings? FanCurve { get; init; }

    /// <summary>"turbo", "cycle", "gui" or "none" (null: turbo).</summary>
    public string? NitroKeyAction { get; init; }

    [JsonIgnore]
    public Control.NitroKeyAction EffectiveNitroKeyAction =>
        NitroKeyActions.Parse(NitroKeyAction) ?? Control.NitroKeyAction.Turbo;

    [JsonIgnore]
    public FanCurveSettings EffectiveFanCurve => FanCurve ?? FanCurveSettings.Default;
}

/// <summary>
/// The state file (default /var/lib/nitropower/state.json). A missing or
/// unreadable file gives the default state; writes replace the file atomically.
/// </summary>
public sealed class StateStore(string path)
{
    public const string DefaultPath = "/var/lib/nitropower/state.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public NitroState Load()
    {
        try
        {
            var state = JsonSerializer.Deserialize<NitroState>(File.ReadAllText(Path), Json);
            return state is { SchemaVersion: NitroState.CurrentSchemaVersion } ? state : new NitroState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new NitroState();
        }
    }

    public void Save(NitroState state)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = $"{Path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json) + "\n");
        File.Move(tmp, Path, overwrite: true);
    }
}
