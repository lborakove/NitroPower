using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Gui.Localization;
using NitroPower.Gui.Services;

namespace NitroPower.Gui.ViewModels;

public sealed record ToastItem(string Text, bool IsError);

/// <summary>A yes/no question shown over the window.</summary>
public sealed class ConfirmRequest(string title, string text, string confirm, bool danger)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public string ConfirmText { get; } = confirm;
    public bool IsDanger { get; } = danger;
    public TaskCompletionSource<bool> Answer { get; } = new();
}

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly string _settingsPath;

    public MainViewModel(NitroSession session, string settingsPath)
    {
        Session = session;
        _settingsPath = settingsPath;
        Settings = AppSettings.Load(settingsPath);
        Loc.Instance.SetLanguage(Settings.Language);

        Dashboard = new DashboardViewModel(this);
        Performance = new PerformanceViewModel(this);
        Fans = new FansViewModel(this);
        Battery = new BatteryViewModel(this);
        Device = new DeviceViewModel(this);
        NitroKey = new NitroKeyViewModel(this);
        Presets = new PresetsViewModel(this);
        System = new SystemViewModel(this);
        Pages = [Dashboard, Performance, Fans, Battery, Device, NitroKey, Presets, System];
        CurrentPage = Dashboard;

        session.PropertyChanged += OnSessionChanged;
        session.Notice += (text, error) => Toast(text, error);
        Loc.Instance.Changed += OnLanguageChanged;
        session.SensorInterval = TimeSpan.FromMilliseconds(Settings.RefreshMs);
    }

    public NitroSession Session { get; }

    public DashboardViewModel Dashboard { get; }
    public PerformanceViewModel Performance { get; }
    public FansViewModel Fans { get; }
    public BatteryViewModel Battery { get; }
    public DeviceViewModel Device { get; }
    public NitroKeyViewModel NitroKey { get; }
    public PresetsViewModel Presets { get; }
    public SystemViewModel System { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    partial void OnCurrentPageChanged(PageViewModel value) => value.OnShown();

    public AppSettings Settings { get; private set; }

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var next = change(Settings);
        if (next == Settings)
            return;
        var old = Settings;
        Settings = next;
        next.Save(_settingsPath);
        if (next.Language != old.Language)
            Loc.Instance.SetLanguage(next.Language);
        if (next.Theme != old.Theme)
            ApplyTheme();
        if (next.RefreshMs != old.RefreshMs)
            Session.SensorInterval = TimeSpan.FromMilliseconds(next.RefreshMs);
        OnPropertyChanged(nameof(Settings));
    }

    public void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Settings.Theme switch
            {
                "light" => ThemeVariant.Light,
                "system" => ThemeVariant.Default,
                _ => ThemeVariant.Dark,
            };
        }
    }

    public void ShowPage(string key)
    {
        if (Pages.FirstOrDefault(p => p.Key == key) is { } page)
            CurrentPage = page;
    }

    private NitroStatus? S => Session.Status;

    public string ModelText => S?.ProfileName ?? S?.Model ?? "NitroPower";

    public bool IsTurbo => S?.Turbo == true;

    public string ModeText => IsTurbo ? Loc.T("mode.turbo") : Visuals.ModeName(S?.Mode);

    public IBrush ModeBrush => Visuals.ModeBrush(IsTurbo ? "turbo" : S?.Mode);

    public IBrush ModeSoftBrush => Visuals.ModeSoftBrush(IsTurbo ? "turbo" : S?.Mode);

    public Geometry? ModeIcon => Visuals.Icon(Visuals.ModeIconKey(IsTurbo ? "turbo" : S?.Mode));

    public bool HasStatus => S is not null;

    public string PowerText => S?.Power is { } p
        ? (p.OnAc ? Loc.T("power.ac") : Loc.T("power.battery")) + (p.BatteryPercent is { } pct ? $" · {pct}%" : "")
        : "—";

    public Geometry? PowerIcon => Visuals.Icon(S?.Power.OnAc == true ? "ico.plug" : "ico.battery");

    public string? GpuOcText => S?.Gpu is { OverclockEnabled: true, Wanted: { IsZero: false } w }
        ? $"GPU {Visuals.Offset(w.Core)}/{Visuals.Offset(w.Mem)}"
        : null;

    public string FansText => Visuals.FanChoice(S?.FanChoice);

    public bool ShowBanner => Session.Link is LinkState.NotRunning or LinkState.NoPermission;

    public string BannerTitle => Session.Link == LinkState.NoPermission ? Loc.T("banner.noPermission") : Loc.T("banner.notRunning");

    public string BannerText => Session.Link != LinkState.NoPermission
        ? Loc.T("banner.notRunning.text")
        : ListedInGroup() ? Loc.T("banner.relogin.text") : Loc.T("banner.noPermission.text");

    public string? BannerCommand => Session.Link != LinkState.NoPermission
        ? "sudo systemctl enable --now nitropowerd"
        : ListedInGroup() ? null : "sudo usermod -aG nitropower $USER";

    /// <summary>The user is in /etc/group already, but this session started before (log out and in).</summary>
    private static bool ListedInGroup()
    {
        try
        {
            var line = File.ReadLines("/etc/group").FirstOrDefault(l => l.StartsWith(NitroApiProtocol.Group + ":", StringComparison.Ordinal));
            return line?.Split(':') is [_, _, _, var members] && members.Split(',').Contains(Environment.UserName);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public string LinkText => Session.Link switch
    {
        LinkState.Connected => Loc.T("link.connected"),
        LinkState.Demo => Loc.T("link.demo"),
        LinkState.NoPermission => Loc.T("link.noPermission"),
        LinkState.NotRunning => Loc.T("link.notRunning"),
        _ => Loc.T("link.connecting"),
    };

    public bool LinkOk => Session.IsConnected;

    public string VersionText => $"NitroPower {NitroApiProtocol.SoftwareVersion}";

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NitroSession.Status) when Session.Status is { } status:
                foreach (var page in Pages)
                    page.Sync(status);
                RefreshHeader();
                break;
            case nameof(NitroSession.Sensors) when Session.Sensors is { } sensors:
                foreach (var page in Pages)
                    page.OnSensors(sensors);
                break;
            case nameof(NitroSession.Link):
                OnPropertyChanged(nameof(ShowBanner));
                OnPropertyChanged(nameof(BannerTitle));
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(BannerCommand));
                OnPropertyChanged(nameof(LinkText));
                OnPropertyChanged(nameof(LinkOk));
                break;
        }
    }

    private void RefreshHeader()
    {
        foreach (var name in new[]
                 {
                     nameof(ModelText), nameof(IsTurbo), nameof(ModeText), nameof(ModeBrush), nameof(ModeSoftBrush),
                     nameof(ModeIcon), nameof(HasStatus), nameof(PowerText), nameof(PowerIcon), nameof(GpuOcText),
                     nameof(FansText),
                 })
            OnPropertyChanged(name);
    }

    private void OnLanguageChanged()
    {
        RefreshAll();
        foreach (var page in Pages)
            page.OnLanguageChanged();
    }

    [RelayCommand]
    public Task ToggleTurbo() => Session.Call(api => api.ToggleTurbo());

    public Task SetMode(string id) => Session.Call(api => api.SetMode(id));

    public Task SetFans(string choice) => Session.Do(api => api.SetFans(choice));

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public void Toast(string text, bool error = false)
    {
        var item = new ToastItem(text, error);
        Toasts.Add(item);
        while (Toasts.Count > 3)
            Toasts.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Toasts.Remove(item), TimeSpan.FromSeconds(error ? 7 : 4));
    }

    [RelayCommand]
    private void DismissToast(ToastItem item) => Toasts.Remove(item);

    [ObservableProperty]
    public partial ConfirmRequest? Confirm { get; private set; }

    public async Task<bool> AskAsync(string title, string text, string confirm, bool danger = false)
    {
        Confirm?.Answer.TrySetResult(false);
        var request = new ConfirmRequest(title, text, confirm, danger);
        Confirm = request;
        try
        {
            return await request.Answer.Task;
        }
        finally
        {
            if (Confirm == request)
                Confirm = null;
        }
    }

    [RelayCommand]
    private void Answer(string yes) => Confirm?.Answer.TrySetResult(yes == "yes");
}
