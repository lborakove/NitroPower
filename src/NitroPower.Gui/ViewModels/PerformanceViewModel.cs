using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Profiles;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

/// <summary>Offsets of one GPU overclock level, editable.</summary>
public sealed partial class GpuLevelRow : ObservableObject
{
    public static decimal CoreMin => NitroController.GpuLevelCoreMin;
    public static decimal CoreMax => NitroController.GpuLevelCoreMax;
    public static decimal MemMin => NitroController.GpuLevelMemMin;
    public static decimal MemMax => NitroController.GpuLevelMemMax;

    public int Index { get; init; }

    /// <summary>Modes that use this level, e.g. "Eco, Silencioso".</summary>
    public string Modes { get; init; } = "";

    public GpuClockOffsets ProfileValue { get; init; } = GpuClockOffsets.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    public partial decimal? Core { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    public partial decimal? Mem { get; set; }

    public string ProfileText => $"{Visuals.Offset(ProfileValue.Core)} / {Visuals.Offset(ProfileValue.Mem)}";

    public bool IsChanged => (int)(Core ?? 0) != ProfileValue.Core || (int)(Mem ?? 0) != ProfileValue.Mem;

    public GpuClockOffsets Value => new((int)(Core ?? 0), (int)(Mem ?? 0));
}

/// <summary>Thermal modes, turbo, the mode cycle and the GPU overclock.</summary>
public sealed partial class PerformanceViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Key => "performance";
    public override string IconKey => "ico.speed";

    public ObservableCollection<ModeItem> Modes { get; } = [];

    public bool IsTurbo => Status?.Turbo == true;
    public bool CanTurbo => Status?.Power.OnAc == true || IsTurbo;
    public string TurboState => IsTurbo ? Loc.T("turbo.on") : Loc.T("turbo.off");
    public string TurboAction => IsTurbo ? Loc.T("turbo.turnOff") : Loc.T("turbo.turnOn");

    public string TurboSummary
    {
        get
        {
            if (Status?.TurboConfig is not { } t)
                return "";
            var gpu = Status.Gpu is { Levels: { } levels } && Status.Modes.FirstOrDefault(m => m.Id == t.Mode) is { } mode &&
                      mode.GpuOcLevel < levels.Count && !levels[mode.GpuOcLevel].IsZero
                ? Loc.F("turbo.summary.gpu", Visuals.Offset(levels[mode.GpuOcLevel].Core), Visuals.Offset(levels[mode.GpuOcLevel].Mem))
                : "";
            return Loc.F("turbo.summary", Visuals.ModeName(t.Mode), Visuals.FanChoice(t.Fans)) + gpu;
        }
    }

    public string TurboHint => IsTurbo
        ? Loc.F("turbo.returnsTo", Visuals.ModeName(Status?.TurboReturnsTo))
        : CanTurbo ? Loc.T("turbo.hint") : Loc.T("turbo.needsAc");

    public string CycleAc => Cycle(Status?.ModeCycle?.Ac);
    public string CycleBattery => Cycle(Status?.ModeCycle?.Battery);
    public string CycleFans => Status?.ModeCycle?.FansAutoOn is { Count: > 0 } f
        ? Loc.F("cycle.fansAuto", Visuals.JoinAnd(f.Select(Visuals.ModeName)))
        : "";

    private static string Cycle(IReadOnlyList<string>? ids) =>
        ids is { Count: > 0 } ? string.Join("  →  ", ids.Select(Visuals.ModeName)) : "—";

    public bool HasGpu => Status?.Gpu is not null;
    public bool HasOverclock => Status?.Gpu?.Levels is not null;
    public string GpuName => Status?.Gpu is { } g ? g.OverclockName ?? g.PciId : "";

    [ObservableProperty]
    public partial bool OverclockEnabled { get; set; }

    async partial void OnOverclockEnabledChanged(bool value)
    {
        if (Syncing)
            return;
        await Session.Call(api => api.SetGpuOverclock(value));
    }

    public string OffsetsNow => Status?.Gpu switch
    {
        null => "—",
        { Suspended: true } => Loc.T("gpu.asleep"),
        { Offsets: { } o } => $"{Visuals.Offset(o.Core)} / {Visuals.Offset(o.Mem)} MHz",
        _ => "—",
    };

    public string OffsetsWanted => Status?.Gpu?.Wanted is { } w ? $"{Visuals.Offset(w.Core)} / {Visuals.Offset(w.Mem)} MHz" : "—";

    public string PolicyText => Status?.Gpu?.Policy is { } p
        ? (p.AcOnly ? Loc.F("gpu.policy.ac", p.BatteryStopPercent, p.BatteryResumePercent) : Loc.F("gpu.policy.any", p.BatteryStopPercent, p.BatteryResumePercent))
        : "";

    public ObservableCollection<GpuLevelRow> Levels { get; } = [];

    /// <summary>The levels last read from the daemon: rows are only reloaded
    /// when these change, so edits in progress survive unrelated refreshes.</summary>
    private IReadOnlyList<GpuClockOffsets>? _loadedLevels;

    public bool CustomLevels => Status?.Gpu?.CustomLevels == true;

    [RelayCommand]
    private async Task ApplyLevels()
    {
        var levels = Levels.Select(l => l.Value).ToList();
        var stronger = Levels.Any(l => l.Value.Core > l.ProfileValue.Core || l.Value.Mem > l.ProfileValue.Mem);
        if (stronger && !await Main.AskAsync(Loc.T("gpu.levels.confirm.title"), Loc.T("gpu.levels.confirm.text"), Loc.T("gpu.levels.apply")))
            return;
        if (await Session.Call(api => api.SetGpuLevels(levels)) is { } result)
            Main.Toast(Loc.F("gpu.levels.saved", GpuResult(result)));
    }

    [RelayCommand]
    private async Task ResetLevels()
    {
        if (await Session.Call(api => api.SetGpuLevels(null)) is { } result)
            Main.Toast(Loc.F("gpu.levels.reset.done", GpuResult(result)));
    }

    [RelayCommand]
    private async Task ApplyNow()
    {
        if (await Session.Call(api => api.ApplyGpuOverclock()) is { } result)
            Main.Toast(GpuResult(result), result.Status == GpuApplyStatus.Failed);
    }

    public static string GpuResult(GpuApplyResult r) => r.Status switch
    {
        GpuApplyStatus.Applied => Loc.F("gpu.result.applied", r.Offsets is { } o ? $"{Visuals.Offset(o.Core)}/{Visuals.Offset(o.Mem)}" : ""),
        GpuApplyStatus.Unchanged => Loc.T("gpu.result.unchanged"),
        GpuApplyStatus.Suspended => Loc.T("gpu.result.suspended"),
        GpuApplyStatus.NotApplicable => Loc.T("gpu.result.na"),
        _ => Loc.F("gpu.result.failed", r.Detail ?? ""),
    };

    [RelayCommand]
    private Task ToggleTurbo() => Main.ToggleTurbo();

    [RelayCommand]
    private Task NextMode() => Session.Call(api => api.NextMode());

    protected override void OnStatus(NitroStatus s)
    {
        if (!Modes.Select(m => m.Id).SequenceEqual(s.Modes.Select(m => m.Id)) || Modes.Count == 0)
        {
            Modes.Clear();
            foreach (var info in s.Modes)
                Modes.Add(new ModeItem(info, m => Main.SetMode(m.Id)));
        }
        foreach (var item in Modes)
        {
            item.IsCurrent = s.Mode == item.Id;
            item.IsAvailable = s.Power.OnAc || !item.Info.RequiresAc;
            item.GpuText = s.Gpu?.Levels is { } levels && item.Info.GpuOcLevel < levels.Count && !levels[item.Info.GpuOcLevel].IsZero
                ? Loc.F("mode.gpu", Visuals.Offset(levels[item.Info.GpuOcLevel].Core), Visuals.Offset(levels[item.Info.GpuOcLevel].Mem))
                : null;
        }

        OverclockEnabled = s.Gpu?.OverclockEnabled == true;
        if (s.Gpu?.Levels is { } current && s.Gpu.ProfileLevels is { } stock)
        {
            if (Levels.Count != current.Count)
            {
                Levels.Clear();
                for (var i = 0; i < current.Count; i++)
                {
                    var users = s.Modes.Where(m => m.GpuOcLevel == i).Select(m => Visuals.ModeName(m.Id)).ToList();
                    Levels.Add(new GpuLevelRow
                    {
                        Index = i,
                        Modes = users.Count > 0 ? string.Join(", ", users) : Loc.T("gpu.levels.unused"),
                        ProfileValue = i < stock.Count ? stock[i] : GpuClockOffsets.None,
                    });
                }
            }
            if (_loadedLevels is null || !_loadedLevels.SequenceEqual(current))
            {
                for (var i = 0; i < current.Count; i++)
                {
                    Levels[i].Core = current[i].Core;
                    Levels[i].Mem = current[i].Mem;
                }
                _loadedLevels = current;
            }
        }
        else
        {
            Levels.Clear();
            _loadedLevels = null;
        }
        RefreshAll();
    }

    [RelayCommand]
    private void UndoLevels()
    {
        if (_loadedLevels is not { } loaded)
            return;
        for (var i = 0; i < loaded.Count && i < Levels.Count; i++)
        {
            Levels[i].Core = loaded[i].Core;
            Levels[i].Mem = loaded[i].Mem;
        }
    }

    public override void OnLanguageChanged()
    {
        Modes.Clear();
        Levels.Clear();
        _loadedLevels = null;
        base.OnLanguageChanged();
    }
}
