using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.IO;
using NitroPower.Gui.Localization;
using NitroPower.Gui.Services;

namespace NitroPower.Gui.ViewModels;

public sealed record InfoRow(string Label, string Value);

/// <summary>An option of a ComboBox. Lists of choices are built once and only
/// relabelled when the language changes: a new list given to a ComboBox while
/// it is selecting makes it put the previous item back.</summary>
public sealed class Choice(string value, Func<string> label) : ObservableObject
{
    public string Value { get; } = value;
    public string Label => label();

    public static Choice Translated(string value, string key) => new(value, () => Loc.T(key));

    public void Relocalize() => OnPropertyChanged(nameof(Label));
}

/// <summary>About this machine and NitroPower, and the window's own preferences.</summary>
public sealed partial class SystemViewModel(MainViewModel main) : PageViewModel(main)
{
    private SystemFacts? _facts;

    public override string Key => "system";
    public override string IconKey => "ico.info";

    public IReadOnlyList<InfoRow> Machine
    {
        get
        {
            var s = Status;
            _facts ??= Session.IsDemo
                ? new SystemFacts("6.17.0-demo", "Ubuntu 25.10", "13th Gen Intel(R) Core(TM) i5-13420H", 15.3, "580.95.05", null)
                : SystemFacts.Read(Sysfs.Real);
            var f = _facts;
            return
            [
                new(Loc.T("sys.model"), s?.Model ?? "—"),
                new(Loc.T("sys.profile"), s?.ProfileName ?? Loc.T("sys.noProfile")),
                new(Loc.T("sys.bios"), s?.Bios ?? "—"),
                new(Loc.T("sys.cpu"), f.Cpu ?? "—"),
                new(Loc.T("sys.gpu"), s?.Gpu is { } g ? $"{g.OverclockName ?? "NVIDIA"} ({g.PciId})" : "—"),
                new(Loc.T("sys.memory"), f.MemoryGiB is { } m ? Visuals.N(m, "0.0") + " GiB" : "—"),
                new(Loc.T("sys.os"), f.Distribution ?? "—"),
                new(Loc.T("sys.kernel"), f.Kernel ?? "—"),
                new(Loc.T("sys.nvidia"), f.NvidiaDriver ?? "—"),
            ];
        }
    }

    public IReadOnlyList<InfoRow> Software =>
    [
        new(Loc.T("sys.app"), NitroApiProtocol.SoftwareVersion),
        new(Loc.T("sys.daemon"), Status?.Api is { } api
            ? Session.IsDemo ? Loc.T("link.demo") : Loc.F("sys.daemonVersion", api.Version, api.Protocol)
            : Main.LinkText),
        new(Loc.T("sys.driver"), Status is null ? "—" : Status.DriverLoaded ? Loc.T("sys.driverLoaded") : Loc.T("sys.driverMissing")),
        new(Loc.T("sys.socket"), Session.IsDemo ? "—" : Session.SocketPath),
    ];

    private DriverFacts? _driver;

    public IReadOnlyList<InfoRow> DriverParameters
    {
        get
        {
            _driver ??= Session.IsDemo
                ? new DriverFacts(true, "10AFF059E374CA969ECC0AF",
                [
                    new("cycle_gaming_thermal_profile", "Y"), new("four_zone_kb", "1"), new("nitro_v4", "N"),
                    new("predator_v4", "N"), new("turbo_sense", "-1"),
                ])
                : DriverFacts.Read(Sysfs.Real);
            if (!_driver.Loaded)
                return [new(Loc.T("sys.driver"), Loc.T("sys.driverMissing"))];
            return [new("srcversion", _driver.SrcVersion ?? "—"), .. _driver.Parameters.Select(p => new InfoRow(p.Key, p.Value))];
        }
    }

    [ObservableProperty]
    public partial string? Logs { get; private set; }

    [ObservableProperty]
    public partial bool LogsLoading { get; private set; }

    public bool HasLogs => Logs is not null;

    partial void OnLogsChanged(string? value) => OnPropertyChanged(nameof(HasLogs));

    [RelayCommand]
    private async Task LoadLogs()
    {
        LogsLoading = true;
        try
        {
            Logs = Session.IsDemo
                ? "2026-11-16T01:12:44 nitropowerd[1]: Nitro key: turbo on, mode performance\n" +
                  "2026-11-16T01:14:35 nitropowerd[1]: Nitro key: turbo off, mode performance\n" +
                  "2026-11-16T01:20:42 nitropowerd[1]: fan curve running"
                : await DaemonLog.ReadAsync();
            if (string.IsNullOrWhiteSpace(Logs))
                Logs = Loc.T("sys.logs.empty");
        }
        finally
        {
            LogsLoading = false;
        }
    }

    [RelayCommand]
    private void HideLogs() => Logs = null;

    public IReadOnlyList<Choice> Languages { get; } =
    [
        Choice.Translated(Loc.Auto, "settings.language.auto"),
        new(Loc.Portuguese, () => "Português (Brasil)"),
        new(Loc.English, () => "English"),
    ];

    public IReadOnlyList<Choice> Themes { get; } =
    [
        Choice.Translated("dark", "settings.theme.dark"),
        Choice.Translated("light", "settings.theme.light"),
        Choice.Translated("system", "settings.theme.system"),
    ];

    public IReadOnlyList<Choice> Intervals { get; } =
    [
        Choice.Translated("1000", "settings.refresh.1"),
        Choice.Translated("2000", "settings.refresh.2"),
        Choice.Translated("5000", "settings.refresh.5"),
    ];

    public Choice? Language
    {
        get => Languages.FirstOrDefault(c => c.Value == Main.Settings.Language) ?? Languages[0];
        set
        {
            if (value is null)
                return;
            Main.UpdateSettings(s => s with { Language = value.Value });
            OnPropertyChanged();
        }
    }

    public Choice? Theme
    {
        get => Themes.FirstOrDefault(c => c.Value == Main.Settings.Theme) ?? Themes[0];
        set
        {
            if (value is null)
                return;
            Main.UpdateSettings(s => s with { Theme = value.Value });
            OnPropertyChanged();
        }
    }

    public Choice? Interval
    {
        get => Intervals.FirstOrDefault(c => c.Value == Main.Settings.RefreshMs.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? Intervals[0];
        set
        {
            if (value is null)
                return;
            Main.UpdateSettings(s => s with { RefreshMs = int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture) });
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => Main.Settings.CloseToTray;
        set
        {
            Main.UpdateSettings(s => s with { CloseToTray = value });
            OnPropertyChanged();
        }
    }

    public bool StartAtLogin
    {
        get => Autostart.IsEnabled;
        set
        {
            try
            {
                Autostart.Set(value);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Main.Toast(e.Message, true);
            }
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _facts = null;
        _driver = null;
        Session.RequestRefresh();
        RefreshAll();
    }

    protected override void OnStatus(NitroStatus status)
    {
        OnPropertyChanged(nameof(Machine));
        OnPropertyChanged(nameof(Software));
        OnPropertyChanged(nameof(DriverParameters));
    }

    public override void OnLanguageChanged()
    {
        foreach (var choice in Languages.Concat(Themes).Concat(Intervals))
            choice.Relocalize();
        base.OnLanguageChanged();
    }
}
