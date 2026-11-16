using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.IO;
using NitroPower.Core.Monitoring;
using NitroPower.Gui.Localization;
using NitroPower.Gui.Services;

namespace NitroPower.Gui.ViewModels;

/// <summary>Battery health and charge, the charge limiter, calibration and USB charging.</summary>
public sealed partial class BatteryViewModel : PageViewModel
{
    private DateTime _lastRead;

    public BatteryViewModel(MainViewModel main) : base(main)
    {
        Limiter = new FeatureToggle(NitroFeatures.BatteryLimiter, "ico.shield", ChangeFeature);
        Calibration = new FeatureToggle(NitroFeatures.BatteryCalibration, "ico.refresh", ChangeCalibration);
    }

    public override string Key => "battery";
    public override string IconKey => "ico.battery";

    public FeatureToggle Limiter { get; }
    public FeatureToggle Calibration { get; }

    private async Task<bool> ChangeFeature(FeatureToggle f, bool on) =>
        await Session.Do(api => api.SetFeature(f.Id, on));

    private async Task<bool> ChangeCalibration(FeatureToggle f, bool on)
    {
        if (on && !await Main.AskAsync(Loc.T("battery.calibration.confirm.title"), Loc.T("battery.calibration.confirm.text"),
                Loc.T("battery.calibration.start")))
            return false;
        return await ChangeFeature(f, on);
    }

    [ObservableProperty]
    public partial BatteryFacts? Facts { get; private set; }

    public double? Percent => Facts?.Percent ?? Status?.Power.BatteryPercent;
    public string PercentText => Percent is { } p ? Visuals.N(p, "0") + "%" : "—";
    public bool OnAc => Status?.Power.OnAc == true;

    public string StateText => Facts?.Status switch
    {
        "Charging" => Loc.T("battery.charging"),
        "Discharging" => Loc.T("battery.discharging"),
        "Full" => Loc.T("battery.full"),
        "Not charging" => Limiter.IsOn ? Loc.T("battery.heldByLimiter") : Loc.T("battery.notCharging"),
        null => OnAc ? Loc.T("power.ac") : Loc.T("power.battery"),
        var s => s,
    };

    public string RateText => Facts?.RateW is { } w and > 0.05 ? Visuals.N(w, "0.0") + " W" : "—";

    public string TimeText => Facts?.TimeLeft is { } t
        ? Loc.F(Facts.Status == "Charging" ? "battery.timeToFull" : "battery.timeLeft", (int)t.TotalHours, t.Minutes)
        : "—";

    public string HealthText => Facts?.HealthPercent is { } h ? Visuals.N(h, "0") + "%" : "—";
    public double? Health => Facts?.HealthPercent;
    public string CapacityText => Facts is { EnergyFullWh: { } full }
        ? Facts.EnergyDesignWh is { } design ? Loc.F("battery.capacity", full, design) : Visuals.N(full, "0.0") + " Wh"
        : "—";
    public string EnergyText => Facts?.EnergyNowWh is { } now ? Visuals.N(now, "0.0") + " Wh" : "—";
    public string CyclesText => Facts?.Cycles?.ToString(Loc.Instance.Culture) ?? "—";
    public string VoltageText => Facts?.VoltageV is { } v ? Visuals.N(v, "0.00") + " V" : "—";
    public string ModelText => Facts is null ? "—" : string.Join(" · ",
        new[] { Facts.Manufacturer, Facts.Model, Facts.Technology }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool HasUsbCharging => Status?.UsbCharging is not null;
    public int UsbCharging => Status?.UsbCharging ?? 0;
    public bool Usb0 => UsbCharging == 0;
    public bool Usb10 => UsbCharging == 10;
    public bool Usb20 => UsbCharging == 20;
    public bool Usb30 => UsbCharging == 30;

    [RelayCommand]
    private Task SetUsb(string level) =>
        Session.Do(api => api.SetUsbCharging(int.Parse(level, System.Globalization.CultureInfo.InvariantCulture)));

    private void ReadFacts()
    {
        if (Session.IsDemo)
        {
            Facts = new BatteryFacts("BAT1", Status?.Power.OnAc == true ? "Not charging" : "Discharging", Status?.Power.BatteryPercent,
                43.1, 53.2, 57.5, Status?.Power.OnAc == true ? 0 : 14.6, 16.4, 87, "Li-ion", "AP21D8M", "SMP");
        }
        else
        {
            try
            {
                Facts = BatteryFacts.Read(Sysfs.Real);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Facts = null;
            }
        }
        _lastRead = DateTime.UtcNow;
        RefreshAll();
    }

    protected override void OnStatus(NitroStatus s)
    {
        Limiter.Load(s.Features);
        Calibration.Load(s.Features);
        ReadFacts();
    }

    public override void OnSensors(SensorSnapshot sensors)
    {
        if (Main.CurrentPage == this && DateTime.UtcNow - _lastRead > TimeSpan.FromSeconds(5))
            ReadFacts();
    }

    public override void OnShown() => ReadFacts();

    public override void OnLanguageChanged()
    {
        Limiter.Relocalize();
        Calibration.Relocalize();
        base.OnLanguageChanged();
    }
}
