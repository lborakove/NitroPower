using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Policy;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

/// <summary>Automatic, maximum, fixed speeds, or the software curve with its editor.</summary>
public sealed partial class FansViewModel : PageViewModel
{
    private readonly DispatcherTimer _manualDelay;

    public FansViewModel(MainViewModel main) : base(main)
    {
        _manualDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _manualDelay.Tick += async (_, _) =>
        {
            _manualDelay.Stop();
            await Session.Do(api => api.SetFans(new FanSpeeds(ManualCpu, ManualGpu).ToString()));
        };
    }

    public override string Key => "fans";
    public override string IconKey => "ico.fan";

    /// <summary>"auto", "max", "manual" or "curve".</summary>
    public string Choice => Status?.FanChoice switch
    {
        "auto" or "max" or "curve" => Status.FanChoice,
        null => "",
        _ => "manual",
    };

    public bool IsAuto => Choice == "auto";
    public bool IsMax => Choice == "max";
    public bool IsManual => Choice == "manual" || _manualPending;
    public bool IsCurve => Choice == "curve";

    private bool _manualPending;

    [RelayCommand]
    private async Task Select(string choice)
    {
        _manualPending = choice == "manual";
        OnPropertyChanged(nameof(IsManual));
        switch (choice)
        {
            case "auto" or "max":
                await Session.Do(api => api.SetFans(choice));
                break;
            case "manual":
                await Session.Do(api => api.SetFans(new FanSpeeds(ManualCpu, ManualGpu).ToString()));
                break;
            case "curve":
                await Session.Do(api => api.SetFanCurve(new FanCurveSettings(CpuPoints, GpuPoints)));
                break;
        }
        _manualPending = false;
    }

    [ObservableProperty]
    public partial int ManualCpu { get; set; } = 50;

    [ObservableProperty]
    public partial int ManualGpu { get; set; } = 50;

    [ObservableProperty]
    public partial bool LinkSpeeds { get; set; } = true;

    partial void OnManualCpuChanged(int value)
    {
        if (Syncing)
            return;
        if (LinkSpeeds && ManualGpu != value)
            ManualGpu = value;
        QueueManual();
    }

    partial void OnManualGpuChanged(int value)
    {
        if (Syncing)
            return;
        if (LinkSpeeds && ManualCpu != value)
            ManualCpu = value;
        QueueManual();
    }

    private void QueueManual()
    {
        if (Choice != "manual")
            return;
        _manualDelay.Stop();
        _manualDelay.Start();
    }

    private SensorSnapshot? _now;

    public string CpuRpm => Visuals.Rpm(_now?.CpuFanRpm);
    public string GpuRpm => Visuals.Rpm(_now?.GpuFanRpm);
    public string CpuTemp => Visuals.Temp(_now?.CpuTempC);
    public string GpuTemp => Visuals.Temp(_now?.GpuTempC);

    public string CpuDuty => Duty(Status?.FanDuty?.Cpu);
    public string GpuDuty => Duty(Status?.FanDuty?.Gpu);

    private static string Duty(int? d) => d switch
    {
        null => "—",
        0 => Loc.T("fans.firmware"),
        _ => $"{d}%",
    };

    public bool IsTurbo => Status?.Turbo == true;

    public static readonly IReadOnlyList<FanCurvePoint> QuietCurve = [new(45, 15), new(60, 25), new(70, 40), new(80, 60), new(88, 85), new(95, 100)];
    public static readonly IReadOnlyList<FanCurvePoint> BalancedCurve = FanCurve.Default.Points;
    public static readonly IReadOnlyList<FanCurvePoint> AggressiveCurve = [new(35, 30), new(50, 45), new(60, 60), new(70, 80), new(80, 95), new(88, 100)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingPoints), nameof(CurveDirty), nameof(CurveError), nameof(CurveSummary))]
    public partial IReadOnlyList<FanCurvePoint> CpuPoints { get; set; } = BalancedCurve;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingPoints), nameof(CurveDirty), nameof(CurveError), nameof(CurveSummary))]
    public partial IReadOnlyList<FanCurvePoint> GpuPoints { get; set; } = BalancedCurve;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingPoints), nameof(EditingColor), nameof(MarkerTemp), nameof(CurveSummary), nameof(EditingCpu), nameof(OtherName))]
    public partial bool EditingGpu { get; set; }

    public bool EditingCpu => !EditingGpu;

    [RelayCommand]
    private void Edit(string which) => EditingGpu = which == "gpu";

    public IReadOnlyList<FanCurvePoint> EditingPoints
    {
        get => EditingGpu ? GpuPoints : CpuPoints;
        set
        {
            if (EditingGpu)
                GpuPoints = value;
            else
                CpuPoints = value;
        }
    }

    public Color EditingColor => EditingGpu ? Visuals.Gpu : Visuals.Cpu;

    public string OtherName => EditingGpu ? "CPU" : "GPU";

    public double MarkerTemp => (EditingGpu ? _now?.GpuTempC : _now?.CpuTempC) ?? double.NaN;

    public string CurveSummary => string.Join("   ", EditingPoints.Select(p => $"{p.TempC:0}° → {p.Percent}%"));

    public bool CurveDirty => Status?.FanCurve is not { } saved ||
                              !saved.Cpu.SequenceEqual(CpuPoints) || !saved.Gpu.SequenceEqual(GpuPoints);

    public string? CurveError
    {
        get
        {
            try
            {
                new FanCurveSettings(CpuPoints, GpuPoints).Build();
                return null;
            }
            catch (ArgumentException e)
            {
                return Services.Messages.Translate("unsafe fan curve: " + e.Message);
            }
        }
    }

    [RelayCommand]
    private void UsePreset(string name) => EditingPoints = name switch
    {
        "quiet" => QuietCurve,
        "aggressive" => AggressiveCurve,
        _ => BalancedCurve,
    };

    [RelayCommand]
    private void CopyToOther()
    {
        if (EditingGpu)
            CpuPoints = GpuPoints;
        else
            GpuPoints = CpuPoints;
    }

    [RelayCommand]
    private async Task ApplyCurve()
    {
        if (await Session.Do(api => api.SetFanCurve(new FanCurveSettings(CpuPoints, GpuPoints))))
            Main.Toast(Loc.T("fans.curve.applied"));
    }

    [RelayCommand]
    private void RevertCurve()
    {
        var saved = Status?.FanCurve ?? FanCurveSettings.Default;
        CpuPoints = saved.Cpu;
        GpuPoints = saved.Gpu;
    }

    private FanCurveSettings? _loadedCurve;

    protected override void OnStatus(NitroStatus s)
    {
        if (s.FanChoice is { } c && FanSpeeds.TryParse(c) is { IsAuto: false } speeds && c != "max")
        {
            ManualCpu = speeds.Cpu;
            ManualGpu = speeds.Gpu;
            LinkSpeeds = speeds.Cpu == speeds.Gpu;
        }
        // Reload the editor only when the saved curve changed, keeping edits in progress
        var saved = s.FanCurve ?? FanCurveSettings.Default;
        if (_loadedCurve is null || !_loadedCurve.Equals(saved))
        {
            CpuPoints = saved.Cpu;
            GpuPoints = saved.Gpu;
            _loadedCurve = saved;
        }
        RefreshAll();
    }

    public override void OnSensors(SensorSnapshot sensors)
    {
        _now = sensors;
        OnPropertyChanged(nameof(CpuRpm));
        OnPropertyChanged(nameof(GpuRpm));
        OnPropertyChanged(nameof(CpuTemp));
        OnPropertyChanged(nameof(GpuTemp));
        OnPropertyChanged(nameof(MarkerTemp));
    }
}
