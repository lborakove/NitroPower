using System.Text;
using System.Text.Json;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Api;

public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int Internal = -32603;

    /// <summary>NitroException: the request cannot be met on this model (message for the user).</summary>
    public const int Refused = -32000;

    /// <summary>The driver rejected a write (EINVAL, ENODEV...).</summary>
    public const int DriverRefused = -32001;

    public const int PermissionDenied = -32002;
}

/// <summary>A JSON-RPC answer: the line to send back (null for notifications)
/// and whether the call may have changed something.</summary>
public sealed record RpcReply(string? Response, string? Method, bool Mutating);

/// <summary>
/// JSON-RPC 2.0 over <see cref="INitroApi"/>, one JSON object per line. The
/// method names are the protocol: keep them stable, add new ones, and bump
/// ApiProtocolVersion only for incompatible changes.
/// </summary>
public sealed class RpcDispatcher
{
    public const string SubscribeMethod = "events.subscribe";

    private readonly Dictionary<string, (bool Mutating, Func<JsonElement, object?> Call)> _methods;

    public RpcDispatcher(INitroApi api)
    {
        _methods = new Dictionary<string, (bool, Func<JsonElement, object?>)>(StringComparer.Ordinal)
        {
            ["system.version"] = (false, _ => api.Info()),
            ["status.get"] = (false, _ => api.Status()),
            ["sensors.get"] = (false, _ => api.Sensors()),
            ["mode.set"] = (true, p => api.SetMode(Get<string>(p, "mode"))),
            ["mode.next"] = (true, _ => api.NextMode()),
            ["turbo.set"] = (true, p => api.SetTurbo(Get<bool>(p, "on"))),
            ["turbo.toggle"] = (true, _ => api.ToggleTurbo()),
            ["fans.set"] = (true, p => Void(() => api.SetFans(Get<string>(p, "choice")))),
            ["fans.curve.set"] = (true, p => Void(() => api.SetFanCurve(new FanCurveSettings(
                Get<List<Policy.FanCurvePoint>>(p, "cpu"), Get<List<Policy.FanCurvePoint>>(p, "gpu"))))),
            ["feature.set"] = (true, p => Void(() => api.SetFeature(Get<string>(p, "feature"), Get<bool>(p, "enabled")))),
            ["usbCharging.set"] = (true, p => Void(() => api.SetUsbCharging(Get<int>(p, "threshold")))),
            ["keyboard.effect.set"] = (true, p => Void(() => api.SetKeyboardEffect(Get<KeyboardEffectState>(p, "effect")))),
            ["keyboard.zones.set"] = (true, p => Void(() => api.SetKeyboardZones(Get<PerZoneColors>(p, "zones")))),
            ["gpu.overclock.set"] = (true, p => api.SetGpuOverclock(Get<bool>(p, "enabled"))),
            ["gpu.overclock.apply"] = (true, _ => api.ApplyGpuOverclock()),
            ["gpu.levels.set"] = (true, p => api.SetGpuLevels(Get<List<GpuClockOffsets>>(p, "levels"))),
            ["gpu.levels.reset"] = (true, _ => api.SetGpuLevels(null)),
            ["nitroKey.set"] = (true, p => Void(() => api.SetNitroKeyAction(Get<string>(p, "action")))),
            ["nitroKey.press"] = (true, _ => api.PressNitroKey()),
            ["modeKey.set"] = (true, p => Void(() => api.SetModeKey(Get<string>(p, "behavior")))),
            ["preset.list"] = (false, _ => api.ListPresets()),
            ["preset.get"] = (false, p => api.GetPreset(Get<string>(p, "name"))),
            ["preset.save"] = (true, p => Void(() => api.SavePreset(Get<string>(p, "name")))),
            ["preset.load"] = (true, p => api.LoadPreset(Get<string>(p, "name"))),
            ["preset.delete"] = (true, p => api.DeletePreset(Get<string>(p, "name"))),
        };
    }

    public IReadOnlyCollection<string> Methods => _methods.Keys;

    private static object? Void(Action action)
    {
        action();
        return null;
    }

    private static T Get<T>(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var value))
            throw new RpcParamsException($"missing parameter '{name}'");
        try
        {
            return value.Deserialize<T>(NitroApiProtocol.Json) ?? throw new RpcParamsException($"parameter '{name}' is null");
        }
        catch (JsonException e)
        {
            throw new RpcParamsException($"parameter '{name}': {e.Message}");
        }
    }

    private sealed class RpcParamsException(string message) : Exception(message);

    /// <summary>Handles one request line.</summary>
    public RpcReply Handle(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException e)
        {
            return new RpcReply(Error(null, RpcErrorCodes.ParseError, $"parse error: {e.Message}"), null, false);
        }
        using (doc)
        {
            var root = doc.RootElement;
            JsonElement? id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idEl) ? idEl.Clone() : null;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
                return new RpcReply(Error(id, RpcErrorCodes.InvalidRequest, "invalid request"), null, false);

            var method = methodEl.GetString()!;
            if (!_methods.TryGetValue(method, out var entry))
                return new RpcReply(id is null ? null : Error(id, RpcErrorCodes.MethodNotFound, $"unknown method '{method}'"), method, false);

            var parameters = root.TryGetProperty("params", out var p) ? p : default;
            string response;
            var mutating = entry.Mutating;
            try
            {
                var result = entry.Call(parameters);
                response = Result(id, result);
            }
            catch (RpcParamsException e)
            {
                response = Error(id, RpcErrorCodes.InvalidParams, e.Message);
                mutating = false;
            }
            catch (NitroException e)
            {
                response = Error(id, RpcErrorCodes.Refused, e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                response = Error(id, RpcErrorCodes.PermissionDenied, e.Message);
            }
            catch (IOException e)
            {
                response = Error(id, RpcErrorCodes.DriverRefused, $"the driver refused the change: {e.Message}");
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                response = Error(id, RpcErrorCodes.InvalidParams, e.Message);
                mutating = false;
            }
            return new RpcReply(id is null ? null : response, method, mutating);
        }
    }

    public static string Result(JsonElement? id, object? result) => Write(w =>
    {
        WriteHead(w, id);
        w.WritePropertyName("result");
        JsonSerializer.Serialize(w, result, result?.GetType() ?? typeof(object), NitroApiProtocol.Json);
    });

    public static string Error(JsonElement? id, int code, string message) => Write(w =>
    {
        WriteHead(w, id);
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    public static string Event(NitroEvent e) => Write(w =>
    {
        w.WriteString("jsonrpc", "2.0");
        w.WriteString("method", "event");
        w.WritePropertyName("params");
        JsonSerializer.Serialize(w, e, NitroApiProtocol.Json);
    });

    private static void WriteHead(Utf8JsonWriter w, JsonElement? id)
    {
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        if (id is { } value)
            value.WriteTo(w);
        else
            w.WriteNullValue();
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
