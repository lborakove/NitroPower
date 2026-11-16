using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Api;

/// <summary><see cref="INitroApi"/> over the nitropowerd socket. Not thread-safe:
/// one request at a time per instance.</summary>
public sealed class NitroClient : INitroApi, IDisposable
{
    private readonly Socket _socket;
    private readonly StreamReader _reader;
    private readonly NetworkStream _stream;
    private int _nextId;

    private NitroClient(Socket socket)
    {
        _socket = socket;
        _socket.ReceiveTimeout = 30_000;
        _socket.SendTimeout = 5_000;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _reader = new StreamReader(_stream, new UTF8Encoding(false));
    }

    public enum ConnectFailure
    {
        None,

        /// <summary>No socket: the daemon is not running.</summary>
        NotRunning,

        /// <summary>The user is not in the nitropower group.</summary>
        PermissionDenied,
    }

    public static NitroClient? TryConnect(string path, out ConnectFailure failure)
    {
        failure = ConnectFailure.None;
        if (!File.Exists(path))
        {
            failure = ConnectFailure.NotRunning;
            return null;
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return new NitroClient(socket);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            failure = e.SocketErrorCode == SocketError.AccessDenied ? ConnectFailure.PermissionDenied : ConnectFailure.NotRunning;
            return null;
        }
    }

    private T Call<T>(string method, object? parameters = null)
    {
        var id = ++_nextId;
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }, NitroApiProtocol.Json);
        _stream.Write(Encoding.UTF8.GetBytes(request + "\n"));

        while (true)
        {
            var line = _reader.ReadLine() ?? throw new NitroException("nitropowerd closed the connection");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || idEl.GetInt32() != id)
                continue; // an event, or a stale answer
            if (root.TryGetProperty("error", out var error))
            {
                var code = error.GetProperty("code").GetInt32();
                var message = error.GetProperty("message").GetString() ?? "error";
                throw code switch
                {
                    RpcErrorCodes.InvalidParams => new ArgumentException(message),
                    RpcErrorCodes.PermissionDenied => new UnauthorizedAccessException(message),
                    _ => new NitroException(message),
                };
            }
            var result = root.GetProperty("result");
            return result.ValueKind == JsonValueKind.Null
                ? default!
                : result.Deserialize<T>(NitroApiProtocol.Json)!;
        }
    }

    private void Call(string method, object? parameters = null) => Call<JsonElement?>(method, parameters);

    /// <summary>Blocks reading events until cancelled or the daemon goes away.
    /// Use a separate client for this.</summary>
    public void Subscribe(Action<NitroEvent> onEvent, CancellationToken ct)
    {
        Call<bool>(RpcDispatcher.SubscribeMethod);
        _socket.ReceiveTimeout = 0;
        using var registration = ct.Register(() => _socket.Shutdown(SocketShutdown.Both));
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = _reader.ReadLine();
            }
            catch (IOException)
            {
                break;
            }
            if (line is null)
                break;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("method", out var m) && m.GetString() == "event" &&
                doc.RootElement.GetProperty("params").Deserialize<NitroEvent>(NitroApiProtocol.Json) is { } e)
                onEvent(e);
        }
    }

    public ApiInfo Info() => Call<ApiInfo>("system.version");
    public NitroStatus Status() => Call<NitroStatus>("status.get");
    public SensorSnapshot Sensors() => Call<SensorSnapshot>("sensors.get");
    public ModeChange SetMode(string mode) => Call<ModeChange>("mode.set", new { mode });
    public ModeChange NextMode() => Call<ModeChange>("mode.next");
    public ModeChange SetTurbo(bool on) => Call<ModeChange>("turbo.set", new { on });
    public ModeChange ToggleTurbo() => Call<ModeChange>("turbo.toggle");
    public void SetFans(string choice) => Call("fans.set", new { choice });
    public void SetFanCurve(FanCurveSettings curves) => Call("fans.curve.set", new { cpu = curves.Cpu, gpu = curves.Gpu });
    public void SetFeature(string feature, bool enabled) => Call("feature.set", new { feature, enabled });
    public void SetUsbCharging(int batteryThreshold) => Call("usbCharging.set", new { threshold = batteryThreshold });
    public void SetKeyboardEffect(KeyboardEffectState effect) => Call("keyboard.effect.set", new { effect });
    public void SetKeyboardZones(PerZoneColors zones) => Call("keyboard.zones.set", new { zones });
    public GpuApplyResult SetGpuOverclock(bool enabled) => Call<GpuApplyResult>("gpu.overclock.set", new { enabled });
    public GpuApplyResult ApplyGpuOverclock() => Call<GpuApplyResult>("gpu.overclock.apply");
    public GpuApplyResult SetGpuLevels(IReadOnlyList<GpuClockOffsets>? levels) => levels is null
        ? Call<GpuApplyResult>("gpu.levels.reset")
        : Call<GpuApplyResult>("gpu.levels.set", new { levels });
    public void SetNitroKeyAction(string action) => Call("nitroKey.set", new { action });
    public ModeChange? PressNitroKey() => Call<ModeChange?>("nitroKey.press");
    public void SetModeKey(string behavior) => Call("modeKey.set", new { behavior });
    public IReadOnlyList<string> ListPresets() => Call<List<string>>("preset.list");
    public Preset? GetPreset(string name) => Call<Preset?>("preset.get", new { name });
    public void SavePreset(string name) => Call("preset.save", new { name });
    public IReadOnlyList<PresetResult> LoadPreset(string name) => Call<List<PresetResult>>("preset.load", new { name });
    public bool DeletePreset(string name) => Call<bool>("preset.delete", new { name });

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
