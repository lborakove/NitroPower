# Linuwu-Sense no NitroPower

O NitroPower inclui o driver `linuwu_sense` (em `driver/`) e reimplementa em
C# tudo o que o Linuwu-Sense faz em espaço de usuário. Esta página mapeia cada
função do Linuwu-Sense para o NitroPower e diz o que funciona no ANV15-52.

Base: upstream `4483b6c` com os patches 0001 e 0002 (veja `driver/patches/`).

## Funções do driver

| Função do Linuwu-Sense | Onde fica no NitroPower | ANV15-52 |
|---|---|---|
| `platform_profile` (eco, quiet, balanced, performance, turbo) | `PlatformProfile`, `NitroController.SetMode`; `nitropower-cli mode <id>` | funciona (patch 0001); sem turbo no firmware |
| Tecla de modo (evento WMI 7): ciclo de modos ou alternância de turbo, com `cycle_gaming_thermal_profile` | `ModeCycler` + `NitroController.PressModeKey`; `nitropower-cli mode next` e `turbo toggle`; o parâmetro em `nitropower-cli mode-key` | a tecla Nitro chega como `KEY_PROG1` pelo teclado, não pelo WMI: o driver nunca executa esta lógica aqui. O `nitropowerd` escuta a tecla e executa a ação escolhida (`nitropower-cli nitro-key turbo|cycle|gui|none`) |
| Quiet e eco devolvem as ventoinhas ao automático, na tecla de modo | `ModeCycleConfig.FansAutoOn` | funciona |
| Turbo (`acer_toggle_turbo`): LED de turbo, ventoinhas no máximo, OC por WMI (ajustes 0x05 e 0x07) | `NitroController.SetTurbo`: modo mais alto do perfil, ventoinhas do perfil, OC da GPU pelo NVML no nível desse modo. Desligar restaura modo e ventoinhas | funciona como turbo do NitroPower (desempenho + ventoinhas no máximo + GPU +100/+200). O OC por WMI é dos Predator antigos, e o NitroSense deste modelo usa NvAPI |
| `turbo_sense/turbo_state` (LED de turbo, com `sysfs_notify`) | `TurboSense`; `status`, `probe` e `watch` | não existe: com `turbo_sense=1` o firmware lê 0 e o notebook não tem LED de turbo |
| `fan_speed` (auto, máximo, CPU/GPU em %) | `NitroSenseDevice.SetFanSpeeds`; `nitropower-cli fan` | funciona |
| `battery_limiter` (carga até 80%) | `nitropower-cli battery-limit` | funciona |
| `battery_calibration` | `nitropower-cli battery-calibration` | funciona |
| `usb_charging` (0, 10, 20, 30%) | `nitropower-cli usb-charging` | funciona |
| `backlight_timeout` (luz do teclado apaga em 30 s) | `nitropower-cli keyboard-timeout` | funciona |
| `lcd_override` (LCD overdrive) | `nitropower-cli lcd-overdrive` | o firmware responde `0xFF000001000000`, fora dos valores esperados: provavelmente não suportado |
| `boot_animation_sound` | `nitropower-cli boot-sound` | o firmware responde status 1: provavelmente não suportado |
| `four_zoned_kb/four_zone_mode`: efeitos static, breathing, neon, wave, shifting, zoom, meteor, twinkling (velocidade, brilho, direção, cor) | `FourZoneKeyboard`, `KeyboardEffectState` (mesmas regras de validação do driver); `nitropower-cli keyboard effect` | não existe: com `four_zone_kb=1` o efeito lê tudo zero |
| `four_zoned_kb/per_zone_mode`: cor por zona + brilho | `PerZoneColors`; `nitropower-cli keyboard zones` | não existe: o firmware responde status 1 (erro) nas quatro zonas |
| Estado por fonte de energia (perfil e ventoinhas na tomada e na bateria, em `/etc/predator_state`) | continua no driver (evento WMI de AC) | funciona no driver |
| Estado do teclado em `/etc/four_zone_kb_state` | continua no driver | — |
| hwmon (temperaturas, RPM das ventoinhas) | `Hwmon`, `SensorReader`; `probe` | funciona |
| Controlador RGB HID (ENEK5130, `0CF2:5130`) | não portado: não há esse dispositivo neste modelo | ausente |

## Ferramentas de espaço de usuário

| Ferramenta do Linuwu-Sense | No NitroPower |
|---|---|
| `Linuwu-Sense.py --status` e os ajustes | `nitropower-cli status` e os comandos de cada função |
| `Linuwu-Sense.py --save/--load/--list-profiles` (perfis nomeados) | `nitropower-cli preset save/load/list/show/delete` em `/etc/nitropower/presets`. Também guarda modo, turbo e o overclock |
| `profile-notifier.py` (notificação ao trocar de perfil) | `nitropower-cli watch --notify`. Usa `poll()` no `platform_profile` em vez de reler o arquivo, porque cada leitura é uma chamada SMI ao firmware |
| `osd/linuwu_turbo_osd.py` (aviso de turbo) | o mesmo `watch --notify`: avisa turbo ligado e desligado, do NitroPower e do LED do firmware, e falhas da tecla Nitro (ex.: turbo na bateria). Inicia com a sessão (`/etc/xdg/autostart/nitropower-notify.desktop`) |
| `osd/penguin_sense.py` (monitor de sensores) | `nitropower-cli probe` hoje; painel na interface gráfica (fase 3) |

## Patch 0002: forçar turbo e teclado RGB

Dois parâmetros de módulo, para modelos que faltam na tabela de quirks (ou
para sondar o hardware):

| Parâmetro | Valores | Efeito |
|---|---|---|
| `turbo_sense` | -1 (padrão do modelo), 0, 1 | quirk `turbo`: grupo `turbo_sense` e alternância de turbo pela tecla WMI |
| `four_zone_kb` | -1, 0, 1 | quirk `four_zone_kb`: grupo `four_zoned_kb` |

Uso permanente: `options linuwu_sense four_zone_kb=1` em `/etc/modprobe.d/`.
O NitroPower detecta os grupos pelo sysfs, então nada muda no aplicativo.

## Sondagem do ANV15-52 (BIOS V1.60, 2026-11-16)

O driver foi carregado com `four_zone_kb=1 turbo_sense=1` e os arquivos foram
só lidos:

| Arquivo | Resposta | Conclusão |
|---|---|---|
| `four_zoned_kb/four_zone_mode` | `0,0,0,0,0,0,0` | sem efeitos RGB |
| `four_zoned_kb/per_zone_mode` | `1000000` em cada zona (resposta bruta 1 = erro) e brilho 0 | sem cor por zona |
| `turbo_sense/turbo_state` | `0` | sem LED de turbo |

O perfil marca `rgbKeyboard` e `turboLed` como `none`. Os comandos
`keyboard effect`/`keyboard zones` continuam no NitroPower para os modelos que
têm o recurso.

## Estado e arquivos do NitroPower

| Arquivo | Conteúdo |
|---|---|
| `/var/lib/nitropower/state.json` | turbo ativo e o que restaurar, último modo fora do turbo, overclock desligado pelo usuário |
| `/etc/nitropower/presets/*.json` | presets nomeados |
