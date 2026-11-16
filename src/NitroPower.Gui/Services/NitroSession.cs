using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Monitoring;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.Services;

public enum LinkState
{
    Connecting,
    Connected,

    /// <summary>No socket: nitropowerd is not running.</summary>
    NotRunning,

    /// <summary>The user is not in the nitropower group (yet: log out and in).</summary>
    NoPermission,

    Demo,
}

/// <summary>
/// The window's connection to nitropowerd. Calls run on worker threads, one at
/// a time (the client is not thread-safe); events arrive on a second
/// connection. Everything observable changes on the UI thread.
/// </summary>
public sealed partial class NitroSession : ObservableObject, IDisposable
{
    private readonly string _socketPath;
    private readonly DemoNitroApi? _demo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _sensorTimer;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _refreshTimer;
    private INitroApi? _api;
    private NitroClient? _client;
    private CancellationTokenSource? _events;
    private bool _connecting, _sensorBusy, _statusBusy, _statusAgain, _sensorsEnabled = true;

    public NitroSession(string socketPath, bool demo)
    {
        _socketPath = socketPath;
        if (demo)
        {
            _demo = new DemoNitroApi();
            _demo.Event += e => Dispatcher.UIThread.Post(() => OnEvent(e));
        }
        _sensorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sensorTimer.Tick += (_, _) => _ = PollSensors();
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer.Tick += (_, _) => _ = Connect();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshStatus();
        };
    }

    public string SocketPath => _socketPath;
    public bool IsDemo => _demo is not null;
    public DemoNitroApi? Demo => _demo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    public partial LinkState Link { get; private set; } = LinkState.Connecting;

    public bool IsConnected => Link is LinkState.Connected or LinkState.Demo;

    [ObservableProperty]
    public partial NitroStatus? Status { get; private set; }

    [ObservableProperty]
    public partial SensorSnapshot? Sensors { get; private set; }

    /// <summary>A message for the user (error or confirmation).</summary>
    public event Action<string, bool>? Notice;

    /// <summary>The Nitro key asked for the window.</summary>
    public event Action? ShowRequested;

    public TimeSpan SensorInterval
    {
        get => _sensorTimer.Interval;
        set => _sensorTimer.Interval = value;
    }

    /// <summary>Sensors are read only while the window is shown.</summary>
    public bool SensorsEnabled
    {
        get => _sensorsEnabled;
        set
        {
            _sensorsEnabled = value;
            if (value && IsConnected)
            {
                _sensorTimer.Start();
                _ = PollSensors();
            }
            else
            {
                _sensorTimer.Stop();
            }
        }
    }

    public void Start()
    {
        if (_demo is not null)
        {
            _api = _demo;
            Link = LinkState.Demo;
            OnConnected();
            return;
        }
        _ = Connect();
        _reconnectTimer.Start();
    }

    private async Task Connect()
    {
        if (_connecting || Link == LinkState.Connected)
            return;
        _connecting = true;
        try
        {
            var path = _socketPath;
            var (client, failure) = await Task.Run(() => (NitroClient.TryConnect(path, out var f), f));
            if (client is null)
            {
                Link = failure == NitroClient.ConnectFailure.PermissionDenied ? LinkState.NoPermission : LinkState.NotRunning;
                return;
            }
            var events = await Task.Run(() => NitroClient.TryConnect(path, out _));
            _client = client;
            _api = client;
            Link = LinkState.Connected;
            if (events is not null)
                StartEvents(events);
            OnConnected();
        }
        finally
        {
            _connecting = false;
        }
    }

    private void OnConnected()
    {
        RequestRefresh();
        SensorsEnabled = _sensorsEnabled;
    }

    private void StartEvents(NitroClient events)
    {
        var cts = new CancellationTokenSource();
        _events = cts;
        var thread = new Thread(() =>
        {
            try
            {
                events.Subscribe(e => Dispatcher.UIThread.Post(() => OnEvent(e)), cts.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or NitroException or ObjectDisposedException
                                          or System.Text.Json.JsonException)
            {
            }
            finally
            {
                events.Dispose();
            }
            if (!cts.IsCancellationRequested)
                Dispatcher.UIThread.Post(Lost);
        })
        { IsBackground = true, Name = "nitropower-events" };
        thread.Start();
    }

    /// <summary>The daemon went away (stopped, restarted, upgraded).</summary>
    private void Lost()
    {
        if (Link != LinkState.Connected)
            return;
        _events?.Cancel();
        _events = null;
        _client?.Dispose();
        _client = null;
        _api = null;
        _sensorTimer.Stop();
        Link = LinkState.NotRunning;
    }

    private void OnEvent(NitroEvent e)
    {
        if (e.Type == "nitroKey")
        {
            if (e.Message is { } message)
                Notice?.Invoke(Loc.F("notice.nitroKeyFailed", Messages.Translate(message)), true);
            else if (e.Action == NitroKeyAction.Gui)
                ShowRequested?.Invoke();
        }
        RequestRefresh();
    }

    /// <summary>Fetches the status soon, coalescing bursts of events.</summary>
    public void RequestRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public async Task RefreshStatus()
    {
        if (_statusBusy)
        {
            _statusAgain = true;
            return;
        }
        _statusBusy = true;
        try
        {
            do
            {
                _statusAgain = false;
                var status = await Query(api => api.Status());
                if (status is not null)
                    Status = status;
            } while (_statusAgain);
        }
        finally
        {
            _statusBusy = false;
        }
    }

    private async Task PollSensors()
    {
        if (_sensorBusy || !IsConnected)
            return;
        _sensorBusy = true;
        try
        {
            var snapshot = await Query(api => api.Sensors());
            if (snapshot is not null)
                Sensors = snapshot;
        }
        finally
        {
            _sensorBusy = false;
        }
    }

    /// <summary>A read; failures are quiet (the banner shows the connection).</summary>
    public Task<T?> Query<T>(Func<INitroApi, T> call) where T : class => Run(call, quiet: true);

    /// <summary>A change; a refusal is shown to the user. Returns null on failure.</summary>
    public Task<T?> Call<T>(Func<INitroApi, T> call) where T : class => Run(call, quiet: false);

    /// <summary>A change without a result; true on success.</summary>
    public async Task<bool> Do(Action<INitroApi> action) =>
        await Run(api =>
        {
            action(api);
            return Done.Value;
        }, quiet: false) is not null;

    private sealed class Done
    {
        public static readonly Done Value = new();
    }

    private async Task<T?> Run<T>(Func<INitroApi, T> call, bool quiet) where T : class
    {
        if (_api is not { } api)
        {
            if (!quiet)
                Notice?.Invoke(Loc.T("notice.notConnected"), true);
            return null;
        }
        await _gate.WaitAsync();
        try
        {
            var result = await Task.Run(() => call(api));
            if (!quiet)
                RequestRefresh();
            return result;
        }
        catch (Exception e) when (e is NitroException or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            if (!quiet)
            {
                Notice?.Invoke(Messages.Translate(e.Message), true);
                RequestRefresh();
            }
            return null;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            if (IsDemo)
            {
                if (!quiet)
                    Notice?.Invoke(Messages.Translate(e.Message), true);
                return null;
            }
            Lost();
            if (!quiet)
                Notice?.Invoke(Loc.T("notice.lost"), true);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _sensorTimer.Stop();
        _reconnectTimer.Stop();
        _refreshTimer.Stop();
        _events?.Cancel();
        _client?.Dispose();
    }
}
