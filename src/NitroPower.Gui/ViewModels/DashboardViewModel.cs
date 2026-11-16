using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.Monitoring;
using NitroPower.Gui.Controls;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

/// <summary>Live sensors with two minutes of history, and the quick controls.</summary>
public sealed partial class DashboardViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Key => "dashboard";
    public override string IconKey => "ico.dashboard";

    public ChartSeries CpuTempSeries { get; } = new(Visuals.Cpu);
    public ChartSeries GpuTempSeries { get; } = new(Visuals.Gpu);
    public ChartSeries CpuFanSeries { get; } = new(Visuals.Cpu);
    public ChartSeries GpuFanSeries { get; } = new(Visuals.Gpu);
    public ChartSeries CpuLoadSeries { get; } = new(Visuals.Cpu);
    public ChartSeries GpuLoadSeries { get; } = new(Visuals.Gpu);

    public IReadOnlyList<ChartSeries> TempSeries => [CpuTempSeries, GpuTempSeries];
    public IReadOnlyList<ChartSeries> FanSeries => [CpuFanSeries, GpuFanSeries];
    public IReadOnlyList<ChartSeries> LoadSeries => [CpuLoadSeries, GpuLoadSeries];

    [ObservableProperty]
    public partial int ChartRevision { get; private set; }

    [ObservableProperty]
    public partial SensorSnapshot? Now { get; private set; }

    public double? CpuTemp => Now?.CpuTempC;
    public double? GpuTemp => Now?.GpuTempC;
    public string CpuTempText => Visuals.Temp(Now?.CpuTempC);
    public string GpuTempText => Visuals.Temp(Now?.GpuTempC);
    public string SystemTempText => Visuals.Temp(Now?.SystemTempC);
    public string SsdTempText => Visuals.Temp(Now?.SsdTempC);
    public string CpuFanText => Visuals.Rpm(Now?.CpuFanRpm);
    public string GpuFanText => Visuals.Rpm(Now?.GpuFanRpm);
    public string CpuFanNumber => Now?.CpuFanRpm is { } r ? r.ToString("N0", Loc.Instance.Culture) : "—";
    public string GpuFanNumber => Now?.GpuFanRpm is { } r ? r.ToString("N0", Loc.Instance.Culture) : "—";
    public double? CpuFanValue => Now?.CpuFanRpm;
    public double? GpuFanValue => Now?.GpuFanRpm;
    public double? CpuLoad => Now?.CpuUsagePercent;
    public double? GpuLoad => Now?.Gpu?.UtilizationPercent;
    public string CpuLoadText => Visuals.Percent(Now?.CpuUsagePercent);
    public string GpuLoadText => Now?.GpuAwake == false ? Loc.T("gpu.asleep") : Visuals.Percent(Now?.Gpu?.UtilizationPercent);
    public string CpuFreqText => Now?.CpuFreqAvgMHz is { } avg
        ? Loc.F("dash.freq", Visuals.GHz(avg), Visuals.GHz(Now.CpuFreqMaxMHz))
        : "—";
    public string GpuClockText => Now?.Gpu is { } g
        ? Loc.F("dash.gpuClocks", Visuals.MHz(g.CoreClockMHz), Visuals.MHz(g.MemClockMHz))
        : Now?.GpuAwake == false ? Loc.T("gpu.asleep.long") : "—";
    public string GpuPowerText => Now?.Gpu?.PowerW is { } w
        ? (Now.Gpu.PowerLimitW is { } lim ? $"{Visuals.N(w, "0")} / {Visuals.N(lim, "0")} W" : $"{Visuals.N(w, "0")} W")
        : "—";

    /// <summary>Highest fan speed seen, for the RPM rings.</summary>
    [ObservableProperty]
    public partial double FanScale { get; private set; } = 6000;

    public ObservableCollection<ModeItem> Modes { get; } = [];

    public bool IsTurbo => Status?.Turbo == true;
    public bool CanTurbo => Status?.Power.OnAc == true;
    public string TurboText => IsTurbo ? Loc.T("turbo.on") : Loc.T("turbo.off");
    public string TurboHint => IsTurbo
        ? Loc.F("turbo.returnsTo", Visuals.ModeName(Status?.TurboReturnsTo))
        : CanTurbo ? Loc.T("turbo.hint") : Loc.T("turbo.needsAc");

    public string FanChoiceText => Visuals.FanChoice(Status?.FanChoice);

    public string GpuOcSummary => Status?.Gpu switch
    {
        null or { Levels: null } => "—",
        { OverclockEnabled: false } => Loc.T("common.off"),
        { Wanted.IsZero: true } => Loc.T("dash.gpuOc.none"),
        { Wanted: var w } => $"{Visuals.Offset(w.Core)} / {Visuals.Offset(w.Mem)} MHz",
    };
    public string FanChoice => Status?.FanChoice switch
    {
        "auto" or "max" or "curve" => Status.FanChoice,
        null => "",
        _ => "manual",
    };

    public override void OnSensors(SensorSnapshot s)
    {
        Now = s;
        CpuTempSeries.Add(s.CpuTempC);
        GpuTempSeries.Add(s.GpuTempC);
        CpuFanSeries.Add(s.CpuFanRpm);
        GpuFanSeries.Add(s.GpuFanRpm);
        CpuLoadSeries.Add(s.CpuUsagePercent);
        GpuLoadSeries.Add(s.Gpu?.UtilizationPercent ?? (s.GpuAwake ? null : 0));
        var maxRpm = Math.Max(s.CpuFanRpm ?? 0, s.GpuFanRpm ?? 0);
        if (maxRpm > FanScale)
            FanScale = Math.Ceiling(maxRpm / 500.0) * 500;
        ChartRevision++;
        foreach (var name in new[]
                 {
                     nameof(CpuTemp), nameof(GpuTemp), nameof(CpuTempText), nameof(GpuTempText), nameof(SystemTempText),
                     nameof(SsdTempText), nameof(CpuFanText), nameof(GpuFanText), nameof(CpuFanValue), nameof(GpuFanValue), nameof(CpuFanNumber), nameof(GpuFanNumber),
                     nameof(CpuLoad), nameof(GpuLoad), nameof(CpuLoadText), nameof(GpuLoadText), nameof(CpuFreqText),
                     nameof(GpuClockText), nameof(GpuPowerText),
                 })
            OnPropertyChanged(name);
    }

    protected override void OnStatus(NitroStatus s)
    {
        if (Modes.Select(m => m.Id).SequenceEqual(s.Modes.Select(m => m.Id)) && Modes.Count > 0)
        {
            foreach (var item in Modes)
                Update(item, s);
        }
        else
        {
            Modes.Clear();
            foreach (var info in s.Modes)
            {
                var item = new ModeItem(info, m => Main.SetMode(m.Id));
                Update(item, s);
                Modes.Add(item);
            }
        }
        foreach (var name in new[] { nameof(IsTurbo), nameof(CanTurbo), nameof(TurboText), nameof(TurboHint), nameof(FanChoiceText), nameof(FanChoice), nameof(GpuOcSummary) })
            OnPropertyChanged(name);
    }

    private static void Update(ModeItem item, NitroStatus s)
    {
        item.IsCurrent = s.Mode == item.Id;
        item.IsAvailable = s.Power.OnAc || !item.Info.RequiresAc;
    }

    public override void OnLanguageChanged()
    {
        Modes.Clear();
        base.OnLanguageChanged();
    }

    [RelayCommand]
    private Task ToggleTurbo() => Main.ToggleTurbo();

    [RelayCommand]
    private Task SetFans(string choice)
    {
        if (choice != "manual")
            return Main.SetFans(choice);
        // Manual speeds are set with the sliders: apply the last ones and show them
        Main.ShowPage("fans");
        return Main.Fans.SelectCommand.ExecuteAsync("manual");
    }
}
