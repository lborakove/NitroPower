# API do nitropowerd

- Socket Unix `/run/nitropower/nitropower.sock`, `root:nitropower` 0660.
- JSON-RPC 2.0, uma mensagem JSON por linha (UTF-8, `\n` no fim).
- Nomes de campos em camelCase; enums como texto (`"turbo"`, `"applied"`).
- Versão do protocolo: `system.version` → `protocol` (hoje 1). Métodos novos não
  mudam a versão; mudanças incompatíveis mudam.
- Clientes em C#: `NitroPower.Core.Api.NitroClient` (implementa `INitroApi`).
- Limites: requisição de até 64 KiB (passou disso, a conexão é fechada), até
  32 conexões ao mesmo tempo, e um assinante de eventos que não lê por 2 s é
  desconectado.

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"status.get"}' | socat - UNIX-CONNECT:/run/nitropower/nitropower.sock
```

## Métodos

| Método | Parâmetros | Resultado |
|---|---|---|
| `system.version` | — | `{version, protocol, daemon}` |
| `status.get` | — | `NitroStatus`: modo, modos, turbo, ventoinhas, curva, recursos, carga USB, teclado, GPU, tecla Nitro. Custa várias chamadas ao firmware: pedir sob demanda e após eventos |
| `sensors.get` | — | temperaturas, RPM, uso/frequência da CPU, telemetria da GPU (cache de 3 s) |
| `mode.set` | `{mode}` | `ModeChange` (`"turbo"` em modelo sem modo turbo = ligar o turbo) |
| `mode.next` | — | `ModeChange`: próximo modo do ciclo |
| `turbo.set` | `{on}` | `ModeChange` |
| `turbo.toggle` | — | `ModeChange` |
| `fans.set` | `{choice}`: `auto`, `max`, `"cpu,gpu"`, `curve` | — |
| `fans.curve.set` | `{cpu:[{tempC,percent}], gpu:[...]}` | — (valida e ativa a curva) |
| `feature.set` | `{feature, enabled}`: `batteryLimiter`, `batteryCalibration`, `keyboardTimeout`, `lcdOverdrive`, `bootSound` | — |
| `usbCharging.set` | `{threshold}`: 0, 10, 20, 30 | — |
| `keyboard.effect.set` | `{effect:{effect, speed, brightness, direction, color:{r,g,b}}}` | — |
| `keyboard.zones.set` | `{zones:{zone1..zone4:{r,g,b}, brightness}}` | — |
| `gpu.overclock.set` | `{enabled}` | `GpuApplyResult` |
| `gpu.overclock.apply` | — | `GpuApplyResult` |
| `gpu.levels.set` | `{levels:[{core, mem}, ...]}` um por nível do perfil; núcleo -300..300, memória -1000..1500 | `GpuApplyResult` (guardado em `state.json`, aplicado já) |
| `gpu.levels.reset` | — | `GpuApplyResult` (volta aos níveis do perfil) |
| `nitroKey.set` | `{action}`: `turbo`, `cycle`, `gui`, `none` | — |
| `nitroKey.press` | — | `ModeChange` ou `null` |
| `modeKey.set` | `{behavior}`: `cycle`, `turbo` | — |
| `preset.list` / `preset.get` / `preset.save` / `preset.load` / `preset.delete` | `{name}` | nomes / `Preset` / — / `[PresetResult]` / `bool` |
| `events.subscribe` | — | `true`; a conexão passa a receber eventos |

`ModeChange`: `{from, to, fans, gpu:{status, offsets, detail}, turbo, changed}`.

Partes de `NitroStatus` usadas pela interface:

- `modes[]`: `{id, platformProfile, requiresAc, gpuOcLevel}`;
- `modeCycle`: `{ac[], battery[], fansAutoOn[]}`;
- `gpu`: `{pciId, overclockName, overclockEnabled, suspended, offsets, wanted,
  levels[], profileLevels[], policy:{acOnly, batteryResumePercent,
  batteryStopPercent}, customLevels}`.

## Erros

| Código | Significado |
|---|---|
| -32700, -32600, -32601, -32602 | JSON inválido, pedido inválido, método inexistente, parâmetro inválido |
| -32000 | Pedido impossível neste modelo; `message` é para o usuário (ex.: "turbo needs AC power") |
| -32001 | O driver recusou a escrita |
| -32002 | Sem permissão |

## Eventos

Depois de `events.subscribe`, chegam notificações
`{"jsonrpc":"2.0","method":"event","params":{...}}`:

| `type` | Campos | Quando |
|---|---|---|
| `mode` | `mode`, `turbo` | o `platform_profile` mudou (por qualquer origem) |
| `power` | `onAc`, `batteryPercent` | tomada ligada ou desligada |
| `nitroKey` | `action`, `mode`, `turbo`, `message` | tecla Nitro; `message` quando a ação falhou |
| `firmwareTurbo` | `turbo` | LED de turbo do firmware (modelos com `turbo_sense`) |
| `changed` | `method` | depois de cada chamada que altera algo |
