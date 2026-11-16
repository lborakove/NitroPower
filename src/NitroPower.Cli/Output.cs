using System.Globalization;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Profiles;

namespace NitroPower.Cli;

internal static class Output
{
    public static void Section(string title) => Console.WriteLine($"\n{title}");

    public static void Row(string key, string value) => Console.WriteLine($"  {key,-24} {value}");

    public static string Flag(bool? v) => v switch { true => "on", false => "off", null => "unknown" };

    public static string Temp(double? c) => c is null ? "?" : string.Create(CultureInfo.InvariantCulture, $"{c:0} °C");

    public static string Fans(FanSpeeds? f) => f switch
    {
        null => "unknown",
        { IsAuto: true } => "auto",
        { Cpu: 100, Gpu: 100 } => "max",
        var s => $"CPU {Duty(s.Value.Cpu)} / GPU {Duty(s.Value.Gpu)}",
    };

    private static string Duty(int pct) => pct == 0 ? "auto" : $"{pct}%";

    /// <summary>"auto", "max", "curve" or "cpu,gpu" as shown to the user.</summary>
    public static string FanChoice(string? choice) => choice switch
    {
        null => "unknown",
        FanSetting.CurveText => "curve",
        _ => Fans(FanSetting.TryParse(choice)?.Speeds),
    };

    public static string Offsets(GpuClockOffsets? o) =>
        o is null ? "?" : string.Create(CultureInfo.InvariantCulture, $"core {o.Core:+0;-0;0} MHz, memory {o.Mem:+0;-0;0} MHz");

    public static string Usb(int? threshold) => threshold switch
    {
        null => "unknown",
        0 => "off",
        var t => $"until battery {t}%",
    };

    public static string Effect(KeyboardEffectState? e)
    {
        if (e is null)
            return "unknown";
        var parts = new List<string> { e.Effect.ToString().ToLowerInvariant() };
        if (KeyboardEffectState.UsesColor(e.Effect))
            parts.Add("#" + e.Color.ToHex());
        if (KeyboardEffectState.UsesSpeed(e.Effect))
            parts.Add($"speed {e.Speed}");
        if (KeyboardEffectState.UsesDirection(e.Effect))
            parts.Add(e.Direction == KeyboardDirection.LeftToRight ? "left to right" : "right to left");
        parts.Add($"brightness {e.Brightness}%");
        return string.Join(", ", parts);
    }

    public static string Zones(PerZoneColors? z) =>
        z is null ? "unknown" : string.Join(" ", z.Zones.Select(c => "#" + c.ToHex())) + $", brightness {z.Brightness}%";

    /// <summary>One line per change; GPU results only when there is something to say.</summary>
    public static void Change(ModeChange change)
    {
        if (!change.Changed)
        {
            Console.WriteLine(change.Turbo switch
            {
                true => "Turbo is already on.",
                false => "Turbo is not on.",
                null => $"Mode is already {change.To}.",
            });
            return;
        }
        var head = change.Turbo switch
        {
            true => $"Turbo on: mode {change.To}",
            false => $"Turbo off: mode {change.To}",
            null => change.From == change.To ? $"Mode {change.To}" : $"Mode {change.From ?? "?"} → {change.To}",
        };
        if (change.Fans is { } f)
            head += $", fans {FanChoice(f)}";
        Console.WriteLine(head);
        Gpu(change.Gpu);
    }

    public static void Gpu(GpuApplyResult r)
    {
        switch (r.Status)
        {
            case GpuApplyStatus.Applied:
                Console.WriteLine($"GPU overclock: {Offsets(r.Offsets)}");
                break;
            case GpuApplyStatus.Unchanged:
                Console.WriteLine($"GPU overclock: {Offsets(r.Offsets)} (already in effect)");
                break;
            case GpuApplyStatus.Suspended:
                Console.WriteLine("GPU overclock: GPU is suspended; the offsets apply when it wakes (service).");
                break;
            case GpuApplyStatus.Failed:
                Console.WriteLine($"GPU overclock failed: {r.Detail}");
                break;
        }
    }

    public static string ModeList(IEnumerable<ModeInfo> modes, string? current) =>
        string.Join(" ", modes.Select(m => m.Id == current ? $"[{m.Id}]" : m.Id));
}
