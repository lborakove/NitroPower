using NitroPower.Core;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Input;
using NitroPower.Core.IO;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Profiles;

namespace NitroPower.Daemon;

internal sealed record DaemonOptions(
    string Socket,
    string State,
    string Presets,
    string? Profiles,
    bool NitroKey,
    bool Verbose);

/// <summary>
/// nitropowerd: the only process that writes to the hardware while it runs.
/// Threads: API server (one task per client), sysfs watcher (mode changes,
/// power, GPU overclock), fan curve, Nitro key. Every hardware access takes
/// <see cref="_hw"/>.
/// </summary>
internal sealed class Daemon : IDisposable
{
    private const int WatchTimeoutMs = 5000;
    private const int FanIntervalMs = 2000;
    private const int TelemetryCacheMs = 3000;

    private readonly object _hw = new();
    private readonly DaemonOptions _options;
    private readonly ISysfs _fs = Sysfs.Real;
    private readonly NitroController _c;
    private readonly IGpuControl? _gpu;
    private readonly GpuOverclockService? _gpuOc;
    private readonly FanCurveEngine _fanCurve;
    private readonly RpcServer _server;
    private readonly Hwmon _hwmon;

    private string? _lastMode;
    private bool? _lastTurbo, _lastAc, _lastFirmwareTurbo, _lastCurveActive;
    private string? _lastGpuError;
    private (DateTime At, GpuTelemetry? Value) _telemetry;

    public Daemon(DaemonOptions options)
    {
        _options = options;
        var catalog = ProfileCatalog.Load(MachineInfo.ProfileDirectories(options.Profiles));
        foreach (var error in catalog.Errors)
            Systemd.Warn($"profile: {error}");
        var machine = MachineInfo.Detect(_fs, catalog);
        _gpu = machine.NvidiaGpu is null ? null : new NvmlGpuControl(_fs);
        _c = new NitroController(_fs, machine, new StateStore(options.State), _gpu);
        _gpuOc = _gpu is null ? null : new GpuOverclockService(_c, _gpu);
        _hwmon = new Hwmon(_fs);
        _fanCurve = new FanCurveEngine(_c, ReadFanTemperatures);

        var api = new LocalNitroApi(_c, new PresetStore(options.Presets), new SensorReader(_fs, CachedTelemetry),
            daemon: true, gpuOffsets: () => _gpuOc?.Applied ?? _c.ReadGpuOffsets());
        _server = new RpcServer(options.Socket, new RpcDispatcher(api), _hw, Systemd.Warn)
        {
            Mutated = method => _server!.Broadcast(new NitroEvent("changed") { Method = method }),
        };
    }

    public int Run(CancellationToken ct)
    {
        var m = _c.Machine;
        Systemd.Info($"nitropowerd {NitroApiProtocol.SoftwareVersion} (API {NitroApiProtocol.Version}) on " +
                     $"{m.Dmi["sys_vendor"]} {m.Dmi["product_name"]}, BIOS {m.Dmi["bios_version"]}");
        Systemd.Info(m.Profile is null
            ? "no profile for this model: modes, turbo and the Nitro key are unavailable"
            : $"profile {m.Profile.Id}");
        if (m.NitroSense is null)
            Systemd.Warn("linuwu_sense is not loaded (no nitro_sense/predator_sense)");
        if (!Environment.IsPrivilegedProcess)
            Systemd.Warn("not running as root: hardware writes will fail");

        var gid = RpcServer.FindGroupId(NitroApiProtocol.Group);
        if (gid is null)
            Systemd.Warn($"group '{NitroApiProtocol.Group}' does not exist: the socket is root-only");
        _server.Start(gid);
        var serverTask = _server.RunAsync(ct);
        Systemd.Info($"listening on {_options.Socket}");

        var threads = new List<Thread>
        {
            Start("watch", () => WatchLoop(ct)),
            Start("fans", () => FanLoop(ct)),
        };
        if (_options.NitroKey && m.Profile is not null)
            threads.Add(Start("nitro-key", () => KeyLoop(ct)));

        Systemd.Notify("READY=1\nSTATUS=running");
        ct.WaitHandle.WaitOne();

        Systemd.Notify("STOPPING=1");
        Systemd.Info("stopping");
        foreach (var t in threads)
            t.Join(500); // background threads; the lock below waits for any work in progress
        serverTask.Wait(1000);
        lock (_hw)
            _gpuOc?.Reset();
        return 0;
    }

    private static Thread Start(string name, Action body)
    {
        var t = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                Systemd.Error($"{name} thread failed: {e}");
                Environment.Exit(1); // let systemd restart us in a clean state
            }
        })
        { Name = name, IsBackground = true };
        t.Start();
        return t;
    }

    private void WatchLoop(CancellationToken ct)
    {
        var paths = new List<string> { PlatformProfile.CurrentPath };
        if (_c.FirmwareTurbo is not null)
            paths.Add(TurboSense.StatePath);
        AttributeWatcher? watcher = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<AttributeChange> changes;
                if (watcher is null)
                {
                    watcher = new AttributeWatcher(paths);
                    if (watcher.Paths.Count == 0)
                    {
                        watcher.Dispose();
                        watcher = null;
                        lock (_hw)
                            Tick([]);
                        ct.WaitHandle.WaitOne(WatchTimeoutMs);
                        continue;
                    }
                    changes = watcher.ReadAll();
                }
                else
                {
                    changes = watcher.Wait(WatchTimeoutMs);
                }

                if (changes.Any(c => c.Value is null))
                {
                    // the driver went away (module reload): reopen on the next turn
                    watcher.Dispose();
                    watcher = null;
                    changes = changes.Where(c => c.Value is not null).ToList();
                }
                lock (_hw)
                    Tick(changes);
            }
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private void Tick(IReadOnlyList<AttributeChange> changes)
    {
        var power = PowerSupplies.Read(_fs);
        if (power.OnAc != _lastAc)
        {
            if (_lastAc is not null)
            {
                Systemd.Info(power.OnAc ? "AC connected" : "on battery");
                _server.Broadcast(new NitroEvent("power") { OnAc = power.OnAc, BatteryPercent = power.BatteryPercent });
            }
            _lastAc = power.OnAc;
        }

        if (changes.FirstOrDefault(c => c.Path == PlatformProfile.CurrentPath)?.Value is { } platform)
            OnPlatformProfile(platform);

        if (changes.FirstOrDefault(c => c.Path == TurboSense.StatePath)?.Value is { } ft)
        {
            var on = ft == "1";
            if (on != _lastFirmwareTurbo && _lastFirmwareTurbo is not null)
                _server.Broadcast(new NitroEvent("firmwareTurbo") { Turbo = on });
            _lastFirmwareTurbo = on;
        }

        SyncGpu();
    }

    private void OnPlatformProfile(string platform)
    {
        var profile = _c.Profile;
        var mode = profile?.FindModeByPlatformProfile(platform)?.Id ?? platform;
        var state = _c.State;
        var turboMode = profile?.EffectiveTurbo.Mode;
        if (state.Turbo is not null && mode != turboMode)
        {
            // Turbo was left by someone else (GNOME menu, power-profiles-daemon,
            // the driver on unplugging): put the fans back as they were.
            try
            {
                _c.SetTurbo(false);
                Systemd.Info($"turbo ended by a mode change to {mode}; fans restored to {state.Turbo.PreviousFans}");
            }
            catch (Exception e) when (e is NitroException or IOException or UnauthorizedAccessException)
            {
                Systemd.Warn($"could not restore the fans after turbo: {e.Message}");
            }
            state = _c.State;
        }
        var turbo = state.Turbo is not null && mode == turboMode;
        if (mode == _lastMode && turbo == _lastTurbo)
            return;
        Systemd.Info(turbo ? $"mode {mode} (turbo)" : $"mode {mode}");
        if (_lastMode is not null)
            _server.Broadcast(new NitroEvent("mode") { Mode = mode, Turbo = turbo });
        _lastMode = mode;
        _lastTurbo = turbo;
    }

    private void SyncGpu()
    {
        if (_gpuOc is null)
            return;
        var r = _gpuOc.Sync();
        switch (r.Status)
        {
            case GpuApplyStatus.Applied:
                Systemd.Info($"GPU offsets core {r.Offsets!.Core:+0;-0;0} MHz, memory {r.Offsets.Mem:+0;-0;0} MHz");
                _lastGpuError = null;
                break;
            case GpuApplyStatus.Failed when r.Detail != _lastGpuError:
                Systemd.Warn($"GPU overclock: {r.Detail}");
                _lastGpuError = r.Detail;
                break;
        }
    }

    private GpuTelemetry? CachedTelemetry()
    {
        if (_gpu is null)
            return null;
        if ((DateTime.UtcNow - _telemetry.At).TotalMilliseconds < TelemetryCacheMs)
            return _telemetry.Value;
        GpuTelemetry? value = null;
        try
        {
            using var nvml = NvmlSession.Open();
            value = nvml.GetTelemetry();
        }
        catch (NvmlException)
        {
        }
        _telemetry = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>CPU from coretemp (no firmware call), GPU from the EC.</summary>
    private (double? Cpu, double? Gpu) ReadFanTemperatures()
    {
        var acer = _hwmon.Find("acer");
        var cpu = _hwmon.TemperatureByLabel(_hwmon.Find("coretemp"), "Package id 0") ?? _hwmon.Temperature(acer, 1);
        var gpu = _hwmon.Temperature(acer, 2);
        if (gpu is null && _gpu is { IsAwake: true })
            gpu = CachedTelemetry()?.TemperatureC;
        return (cpu, gpu);
    }

    private void FanLoop(CancellationToken ct)
    {
        while (!ct.WaitHandle.WaitOne(FanIntervalMs))
        {
            lock (_hw)
            {
                try
                {
                    if (_fanCurve.Tick() is { } written)
                        Systemd.Debug($"fan curve: CPU {written.Cpu}% GPU {written.Gpu}%");
                }
                catch (Exception e) when (e is NitroException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Systemd.Warn($"fan curve: {e.Message}");
                }
                if (_fanCurve.IsActive != _lastCurveActive)
                {
                    if (_lastCurveActive is not null || _fanCurve.IsActive)
                        Systemd.Info(_fanCurve.IsActive ? "fan curve running" : "fan curve stopped");
                    _lastCurveActive = _fanCurve.IsActive;
                }
            }
        }
    }

    private void KeyLoop(CancellationToken ct)
    {
        var key = _c.Profile?.NitroKey;
        var listener = new KeyListener(key?.EvdevCode ?? 148, key?.EffectiveDevices ?? NitroKeyInfo.DefaultDevices,
            Systemd.Info);
        listener.Run(OnNitroKey, ct);
    }

    private void OnNitroKey(string device)
    {
        NitroKeyAction action;
        ModeChange? change = null;
        string? error = null;
        lock (_hw)
        {
            action = _c.NitroKeyAction;
            try
            {
                change = _c.PressNitroKey();
            }
            catch (Exception e) when (e is NitroException or IOException or UnauthorizedAccessException)
            {
                error = e.Message;
            }
        }
        Systemd.Info(error is not null
            ? $"Nitro key ({NitroKeyActions.ToText(action)}): {error}"
            : change is null
                ? $"Nitro key ({NitroKeyActions.ToText(action)})"
                : $"Nitro key: {(change.Turbo is { } t ? (t ? "turbo on" : "turbo off") + ", " : "")}mode {change.To}");
        _server.Broadcast(new NitroEvent("nitroKey")
        {
            Action = action,
            Mode = change?.To,
            Turbo = change?.Turbo,
            Message = error,
        });
    }

    public void Dispose() => _server.Dispose();
}
