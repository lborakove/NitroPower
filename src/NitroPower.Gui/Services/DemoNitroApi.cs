using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Gui.Services;

/// <summary>
/// A pretend Nitro with a 4-zone RGB keyboard, for trying the interface
/// (nitropower --demo) and for screenshots. Nothing touches the hardware.
/// Thread-safe: the session calls it from worker threads.
/// </summary>
public sealed class DemoNitroApi : INitroApi
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Preset> _presets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _features = new()
    {
        [NitroFeatures.BatteryLimiter] = true,
        [NitroFeatures.BatteryCalibration] = false,
        [NitroFeatures.KeyboardTimeout] = true,
        [NitroFeatures.LcdOverdrive] = null,
        [NitroFeatures.BootSound] = false,
    };

    private static readonly ModeInfo[] Modes =
    [
        new("eco", "low-power", false, 0),
        new("quiet", "quiet", true, 0),
        new("balanced", "balanced", false, 1),
        new("performance", "performance", true, 2),
    ];

    private static readonly GpuClockOffsets[] ProfileLevels = [new(0, 0), new(0, 0), new(100, 200)];

    private string _mode = "balanced";
    private TurboSnapshot? _turbo;
    private string _fans = "auto";
    private FanCurveSettings _curve = FanCurveSettings.Default;
    private bool _onAc = true;
    private int _battery = 78;
    private int _usb;
    private bool _ocEnabled = true;
    private GpuClockOffsets[] _levels = ProfileLevels;
    private NitroKeyAction _key = NitroKeyAction.Turbo;
    private ModeKeyBehavior? _modeKey;
    private KeyboardEffectState _effect = new(KeyboardEffect.Wave, 4, 80, KeyboardDirection.LeftToRight, Rgb.Black);
    private PerZoneColors _zones = new(new Rgb(0x2D, 0xD4, 0xBF), new Rgb(0x38, 0xBD, 0xF8), new Rgb(0xA7, 0x8B, 0xFA), new Rgb(0xF4, 0x3F, 0x5E), 80);
    private double _cpuTemp = 52, _gpuTemp = 46, _t;
    private double? _cpuUsage;

    public event Action<NitroEvent>? Event;

    public DemoNitroApi()
    {
        _presets["jogos"] = new Preset { Mode = "performance", Turbo = true, Fans = "max", GpuOverclock = true, BatteryLimiter = true };
        _presets["silencioso"] = new Preset { Mode = "quiet", Fans = "curve", FanCurve = FanCurveSettings.Default, GpuOverclock = false, KeyboardTimeout = true };
        _presets["bateria"] = new Preset { Mode = "eco", Fans = "auto", BatteryLimiter = true, UsbCharging = 0 };
    }

    private void Raise(NitroEvent e) => Event?.Invoke(e);

    private void Changed(string method) => Raise(new NitroEvent("changed") { Method = method });

    public ApiInfo Info() => new(NitroApiProtocol.SoftwareVersion, NitroApiProtocol.Version, false);

    public NitroStatus Status()
    {
        lock (_lock)
        {
            var mode = Modes.First(m => m.Id == _mode);
            var want = !_ocEnabled || !_onAc ? GpuClockOffsets.None : _levels[mode.GpuOcLevel];
            return new NitroStatus
            {
                Api = Info(),
                Model = "Acer Nitro (demo)",
                Bios = "V1.60",
                ProfileId = "demo",
                ProfileName = "Acer Nitro V 15 (demo, 4-zone RGB)",
                DriverLoaded = true,
                Power = new PowerState(_onAc, _battery),
                PlatformProfile = mode.PlatformProfile,
                Mode = _mode,
                Modes = Modes,
                Turbo = _turbo is not null,
                TurboReturnsTo = _turbo?.PreviousMode,
                TurboConfig = new TurboConfig("performance", "max"),
                ModeCycle = new ModeCycleConfig(["quiet", "balanced", "performance"], ["eco", "balanced"], ["quiet", "eco"]),
                FanChoice = _fans,
                FanDuty = FanSetting.TryParse(_fans)?.Speeds ?? new FanSpeeds(CurveDuty(_curve.Cpu, _cpuTemp), CurveDuty(_curve.Gpu, _gpuTemp)),
                FanCurve = _curve,
                Features = new Dictionary<string, bool?>(_features),
                UsbCharging = _usb,
                Keyboard = new KeyboardStatus(_effect, _zones),
                Gpu = new GpuStatus("10de:28a0", "GeForce RTX 4060 Laptop", _ocEnabled, false, want, want,
                    _levels, ProfileLevels, new GpuOcPolicyConfig(true, 40, 30)),
                NitroKeyAction = _key,
                DriverModeKey = _modeKey,
            };
        }
    }

    private static int CurveDuty(IReadOnlyList<FanCurvePoint> points, double temp) => new FanCurve(points).Evaluate(temp);

    public SensorSnapshot Sensors()
    {
        lock (_lock)
        {
            _t += 1;
            var heat = _mode switch { "performance" => 74, "balanced" => 62, "quiet" => 55, _ => 48 };
            var cooling = FanSetting.TryParse(_fans)?.Speeds is { IsAuto: false } s ? (s.Cpu + s.Gpu) / 20.0 : 0;
            var target = heat - cooling + 9 * Math.Sin(_t / 9) + 4 * Math.Sin(_t / 2.7);
            _cpuTemp += (target - _cpuTemp) * 0.25;
            _gpuTemp += (target - 7 + 5 * Math.Sin(_t / 6) - _gpuTemp) * 0.2;
            _cpuUsage = Math.Clamp(28 + 22 * Math.Sin(_t / 7) + 9 * Math.Sin(_t / 1.7), 2, 100);
            var duty = FanSetting.TryParse(_fans)?.Speeds is { IsAuto: false } d
                ? d
                : new FanSpeeds(CurveDuty(_curve.Cpu, _cpuTemp), CurveDuty(_curve.Gpu, _gpuTemp));
            var mode = Modes.First(m => m.Id == _mode);
            return new SensorSnapshot(
                DateTimeOffset.Now, mode.PlatformProfile, new PowerState(_onAc, _battery),
                Math.Round(_cpuTemp), Math.Round(_gpuTemp), 41 + 3 * Math.Sin(_t / 20), 38,
                (int)(duty.Cpu * 62 + 400), (int)(duty.Gpu * 60 + 400),
                _cpuUsage, (int)(2400 + 1400 * Math.Sin(_t / 5)), 4600, true,
                new GpuTelemetry((int)Math.Round(_gpuTemp), 18 + 30 * Math.Abs(Math.Sin(_t / 8)), 75,
                    (int)(1200 + 900 * Math.Abs(Math.Sin(_t / 8))), 8001, (int)(35 + 30 * Math.Sin(_t / 8))));
        }
    }

    public ModeChange SetMode(string mode)
    {
        ModeChange change;
        lock (_lock)
        {
            var info = Modes.FirstOrDefault(m => m.Id == mode) ?? throw new NitroException($"unknown mode '{mode}'");
            if (info.RequiresAc && !_onAc)
                throw new NitroException($"mode {mode} needs AC power");
            var from = _mode;
            string? fans = null;
            if (_turbo is { } t && mode != "performance")
            {
                fans = _fans = t.PreviousFans;
                _turbo = null;
            }
            _mode = mode;
            change = new ModeChange(from, mode, fans, new GpuApplyResult(GpuApplyStatus.Applied)) { Changed = from != mode };
        }
        Raise(new NitroEvent("mode") { Mode = mode, Turbo = _turbo is not null });
        Changed("mode.set");
        return change;
    }

    public ModeChange NextMode()
    {
        string next;
        lock (_lock)
        {
            string[] cycle = _onAc ? ["quiet", "balanced", "performance"] : ["eco", "balanced"];
            next = cycle[(Array.IndexOf(cycle, _mode) + 1) % cycle.Length];
            if (next is "quiet" or "eco")
                _fans = "auto";
        }
        return SetMode(next);
    }

    public ModeChange SetTurbo(bool on)
    {
        ModeChange change;
        lock (_lock)
        {
            if (on == (_turbo is not null))
                return new ModeChange(_mode, _mode, null, new GpuApplyResult(GpuApplyStatus.Unchanged)) { Turbo = on, Changed = false };
            if (on)
            {
                if (!_onAc)
                    throw new NitroException("turbo needs AC power");
                _turbo = new TurboSnapshot(_mode, _fans);
                change = new ModeChange(_mode, "performance", "max", new GpuApplyResult(GpuApplyStatus.Applied)) { Turbo = true };
                _mode = "performance";
                _fans = "max";
            }
            else
            {
                var t = _turbo!;
                change = new ModeChange(_mode, t.PreviousMode, t.PreviousFans, new GpuApplyResult(GpuApplyStatus.Applied)) { Turbo = false };
                _mode = t.PreviousMode;
                _fans = t.PreviousFans;
                _turbo = null;
            }
        }
        Raise(new NitroEvent("mode") { Mode = change.To, Turbo = on });
        Changed("turbo.set");
        return change;
    }

    public ModeChange ToggleTurbo()
    {
        bool on;
        lock (_lock)
            on = _turbo is null;
        return SetTurbo(on);
    }

    public void SetFans(string choice)
    {
        var setting = FanSetting.Parse(choice);
        lock (_lock)
        {
            _fans = setting.ToText() ?? _fans;
            if (_turbo is { } t)
                _turbo = t with { PreviousFans = _fans };
        }
        Changed("fans.set");
    }

    public void SetFanCurve(FanCurveSettings curves)
    {
        curves.Build();
        lock (_lock)
        {
            _curve = curves;
            _fans = "curve";
        }
        Changed("fans.curve.set");
    }

    public void SetFeature(string feature, bool enabled)
    {
        lock (_lock)
        {
            if (!_features.TryGetValue(feature, out var current))
                throw new NitroException($"unknown feature '{feature}'");
            if (current is null)
                throw new IOException("the firmware refused the change");
            _features[feature] = enabled;
        }
        Changed("feature.set");
    }

    public void SetUsbCharging(int batteryThreshold)
    {
        if (!NitroSenseDevice.UsbChargingLevels.Contains(batteryThreshold))
            throw new ArgumentException("threshold must be 0, 10, 20 or 30");
        lock (_lock)
            _usb = batteryThreshold;
        Changed("usbCharging.set");
    }

    public void SetKeyboardEffect(KeyboardEffectState effect)
    {
        effect.Validate();
        lock (_lock)
            _effect = effect.Normalize();
        Changed("keyboard.effect.set");
    }

    public void SetKeyboardZones(PerZoneColors zones)
    {
        lock (_lock)
            _zones = zones;
        Changed("keyboard.zones.set");
    }

    public GpuApplyResult SetGpuOverclock(bool enabled)
    {
        lock (_lock)
            _ocEnabled = enabled;
        Changed("gpu.overclock.set");
        return new GpuApplyResult(GpuApplyStatus.Applied);
    }

    public GpuApplyResult ApplyGpuOverclock() => new(GpuApplyStatus.Unchanged);

    public GpuApplyResult SetGpuLevels(IReadOnlyList<GpuClockOffsets>? levels)
    {
        if (levels is not null && levels.Count != ProfileLevels.Length)
            throw new NitroException($"expected {ProfileLevels.Length} levels, got {levels.Count}");
        lock (_lock)
            _levels = levels?.ToArray() ?? ProfileLevels;
        Changed("gpu.levels.set");
        return new GpuApplyResult(GpuApplyStatus.Applied);
    }

    public void SetNitroKeyAction(string action)
    {
        lock (_lock)
            _key = NitroKeyActions.Parse(action) ?? throw new NitroException($"'{action}' is not turbo, cycle, gui or none");
        Changed("nitroKey.set");
    }

    public ModeChange? PressNitroKey()
    {
        NitroKeyAction key;
        lock (_lock)
            key = _key;
        var result = key switch
        {
            NitroKeyAction.Turbo => ToggleTurbo(),
            NitroKeyAction.Cycle => NextMode(),
            _ => null,
        };
        Raise(new NitroEvent("nitroKey") { Action = key, Mode = _mode, Turbo = _turbo is not null });
        return result;
    }

    public void SetModeKey(string behavior)
    {
        lock (_lock)
        {
            if (_modeKey is null)
                throw new NitroException("the driver has no mode key");
            _modeKey = behavior switch
            {
                "cycle" => ModeKeyBehavior.Cycle,
                "turbo" => ModeKeyBehavior.TurboToggle,
                _ => throw new NitroException($"'{behavior}' is not cycle or turbo"),
            };
        }
        Changed("modeKey.set");
    }

    /// <summary>Acts like a model whose driver handles the WMI mode key (the ANV15-52 does not).</summary>
    public void SimulateDriverModeKey()
    {
        lock (_lock)
            _modeKey ??= ModeKeyBehavior.Cycle;
        Changed("modeKey.set");
    }

    public IReadOnlyList<string> ListPresets()
    {
        lock (_lock)
            return _presets.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public Preset? GetPreset(string name)
    {
        lock (_lock)
            return _presets.GetValueOrDefault(name);
    }

    public void SavePreset(string name)
    {
        if (!PresetStore.IsValidName(name))
            throw new NitroException($"invalid preset name '{name}' (letters, digits, - _ .)");
        lock (_lock)
        {
            _presets[name] = new Preset
            {
                Mode = _turbo?.PreviousMode ?? _mode,
                Turbo = _turbo is not null,
                Fans = _turbo?.PreviousFans ?? _fans,
                FanCurve = _curve,
                GpuOverclock = _ocEnabled,
                BatteryLimiter = _features[NitroFeatures.BatteryLimiter],
                UsbCharging = _usb,
                KeyboardTimeout = _features[NitroFeatures.KeyboardTimeout],
                BootSound = _features[NitroFeatures.BootSound],
                KeyboardEffect = _effect.ToSysfs(),
            };
        }
        Changed("preset.save");
    }

    public IReadOnlyList<PresetResult> LoadPreset(string name)
    {
        var p = GetPreset(name) ?? throw new NitroException($"no preset '{name}'");
        var results = new List<PresetResult>();
        void Step(string what, Action action)
        {
            try
            {
                action();
                results.Add(new PresetResult(what, null));
            }
            catch (Exception e) when (e is NitroException or IOException or ArgumentException)
            {
                results.Add(new PresetResult(what, e.Message));
            }
        }
        if (p.Mode is { } m)
            Step("mode", () => SetMode(m));
        if (p.FanCurve is { } c)
            Step("fan curve", () => SetFanCurve(c));
        if (p.Fans is { } f)
            Step("fans", () => SetFans(f));
        if (p.Turbo is { } t)
            Step("turbo", () => SetTurbo(t));
        if (p.GpuOverclock is { } oc)
            Step("gpu overclock", () => SetGpuOverclock(oc));
        if (p.BatteryLimiter is { } bl)
            Step("battery limiter", () => SetFeature(NitroFeatures.BatteryLimiter, bl));
        if (p.UsbCharging is { } usb)
            Step("usb charging", () => SetUsbCharging(usb));
        if (p.KeyboardTimeout is { } kt)
            Step("keyboard timeout", () => SetFeature(NitroFeatures.KeyboardTimeout, kt));
        if (p.LcdOverdrive is { } lcd)
            Step("lcd overdrive", () => SetFeature(NitroFeatures.LcdOverdrive, lcd));
        return results;
    }

    public bool DeletePreset(string name)
    {
        bool removed;
        lock (_lock)
            removed = _presets.Remove(name);
        Changed("preset.delete");
        return removed;
    }

    /// <summary>Demo only: unplug or plug the charger.</summary>
    public void SetPower(bool onAc)
    {
        lock (_lock)
        {
            _onAc = onAc;
            if (!onAc && _turbo is not null)
            {
                _mode = _turbo.PreviousMode is "quiet" or "performance" ? "balanced" : _turbo.PreviousMode;
                _fans = _turbo.PreviousFans;
                _turbo = null;
            }
            if (!onAc && _mode is "quiet" or "performance")
                _mode = "balanced";
        }
        Raise(new NitroEvent("power") { OnAc = onAc, BatteryPercent = _battery });
    }
}
