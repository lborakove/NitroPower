# nitropowerd API

- Unix socket `/run/nitropower/nitropower.sock`, `root:nitropower` 0660.
- JSON-RPC 2.0, one JSON message per line (UTF-8, terminated by `\n`).
- Field names in camelCase; enums as strings (`"turbo"`, `"applied"`).
- Protocol version: `system.version` → `protocol` (currently 1). New methods do
  not change the version; incompatible changes do.
- C# clients: `NitroPower.Core.Api.NitroClient` (implements `INitroApi`).
- Limits: requests of up to 64 KiB (anything larger closes the connection), up
  to 32 simultaneous connections, and an event subscriber that does not read
  for 2 s is disconnected.

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"status.get"}' | socat - UNIX-CONNECT:/run/nitropower/nitropower.sock
```

## Methods

| Method | Parameters | Result |
| --- | --- | --- |
| `system.version` | — | `{version, protocol, daemon}` |
| `status.get` | — | `NitroStatus`: mode, modes, turbo, fans, curve, features, USB charging, keyboard, GPU, Nitro key. It costs several firmware calls: request it on demand and after events |
| `sensors.get` | — | temperatures, RPM, CPU load/clocks, GPU telemetry (3 s cache) |
| `mode.set` | `{mode}` | `ModeChange` (`"turbo"` on a model without a turbo mode = turn turbo on) |
| `mode.next` | — | `ModeChange`: next mode in the cycle |
| `turbo.set` | `{on}` | `ModeChange` |
| `turbo.toggle` | — | `ModeChange` |
| `fans.set` | `{choice}`: `auto`, `max`, `"cpu,gpu"`, `curve` | — |
| `fans.curve.set` | `{cpu:[{tempC,percent}], gpu:[...]}` | — (validates and enables the curve) |
| `feature.set` | `{feature, enabled}`: `batteryLimiter`, `batteryCalibration`, `keyboardTimeout`, `lcdOverdrive`, `bootSound` | — |
| `usbCharging.set` | `{threshold}`: 0, 10, 20, 30 | — |
| `keyboard.effect.set` | `{effect:{effect, speed, brightness, direction, color:{r,g,b}}}` | — |
| `keyboard.zones.set` | `{zones:{zone1..zone4:{r,g,b}, brightness}}` | — |
| `gpu.overclock.set` | `{enabled}` | `GpuApplyResult` |
| `gpu.overclock.apply` | — | `GpuApplyResult` |
| `gpu.levels.set` | `{levels:[{core, mem}, ...]}`, one per profile level; core -300..300, memory -1000..1500 | `GpuApplyResult` (saved in `state.json` and applied at once) |
| `gpu.levels.reset` | — | `GpuApplyResult` (back to the profile's levels) |
| `nitroKey.set` | `{action}`: `turbo`, `cycle`, `gui`, `none` | — |
| `nitroKey.press` | — | `ModeChange` or `null` |
| `modeKey.set` | `{behavior}`: `cycle`, `turbo` | — |
| `preset.list` / `preset.get` / `preset.save` / `preset.load` / `preset.delete` | `{name}` | names / `Preset` / — / `[PresetResult]` / `bool` |
| `events.subscribe` | — | `true`; the connection starts receiving events |

`ModeChange`: `{from, to, fans, gpu:{status, offsets, detail}, turbo, changed}`.

Parts of `NitroStatus` used by the interface:

- `modes[]`: `{id, platformProfile, requiresAc, gpuOcLevel}`;
- `modeCycle`: `{ac[], battery[], fansAutoOn[]}`;
- `gpu`: `{pciId, overclockName, overclockEnabled, suspended, offsets, wanted,
  levels[], profileLevels[], policy:{acOnly, batteryResumePercent,
  batteryStopPercent}, customLevels}`.

## Errors

| Code | Meaning |
| --- | --- |
| -32700, -32600, -32601, -32602 | Parse error, invalid request, method not found, invalid params |
| -32000 | Not possible on this model; `message` is meant for the user (e.g. "turbo needs AC power") |
| -32001 | The driver refused the write |
| -32002 | Permission denied |

## Events

After `events.subscribe`, notifications arrive as
`{"jsonrpc":"2.0","method":"event","params":{...}}`:

| `type` | Fields | When |
| --- | --- | --- |
| `mode` | `mode`, `turbo` | `platform_profile` changed (from any source) |
| `power` | `onAc`, `batteryPercent` | AC plugged in or unplugged |
| `nitroKey` | `action`, `mode`, `turbo`, `message` | Nitro key; `message` when the action failed |
| `firmwareTurbo` | `turbo` | firmware turbo LED (models with `turbo_sense`) |
| `changed` | `method` | after every call that changes something |
