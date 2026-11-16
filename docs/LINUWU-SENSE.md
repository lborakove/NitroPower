# Linuwu-Sense in NitroPower

NitroPower bundles the `linuwu_sense` driver (in `driver/`) and reimplements in
C# everything Linuwu-Sense does in user space. This page maps each Linuwu-Sense
feature to NitroPower and states what works on the ANV15-52.

Base: upstream `4483b6c` with patches 0001 and 0002 (see `driver/patches/`).

## Driver features

| Linuwu-Sense feature | Where it lives in NitroPower | ANV15-52 |
| --- | --- | --- |
| `platform_profile` (eco, quiet, balanced, performance, turbo) | `PlatformProfile`, `NitroController.SetMode`; `nitropower-cli mode <id>` | works (patch 0001); no turbo in the firmware |
| Mode key (WMI event 7): cycle modes or toggle turbo, with `cycle_gaming_thermal_profile` | `ModeCycler` + `NitroController.PressModeKey`; `nitropower-cli mode next` and `turbo toggle`; the parameter through `nitropower-cli mode-key` | the Nitro key arrives as `KEY_PROG1` from the keyboard, not through WMI, so the driver never runs this logic here. `nitropowerd` listens for the key and runs the chosen action (`nitropower-cli nitro-key turbo\|cycle\|gui\|none`) |
| Quiet and eco return the fans to automatic, on the mode key | `ModeCycleConfig.FansAutoOn` | works |
| Turbo (`acer_toggle_turbo`): turbo LED, fans at maximum, WMI overclock (settings 0x05 and 0x07) | `NitroController.SetTurbo`: the profile's top mode, the profile's fan setting, GPU overclock through NVML at that mode's level. Turning it off restores mode and fans | works as NitroPower's turbo (performance + fans at maximum + GPU +100/+200). The WMI overclock belongs to older Predator models, and this model's NitroSense uses NvAPI |
| `turbo_sense/turbo_state` (turbo LED, with `sysfs_notify`) | `TurboSense`; `status`, `probe` and `watch` | absent: with `turbo_sense=1` the firmware reads 0 and the laptop has no turbo LED |
| `fan_speed` (auto, maximum, CPU/GPU in %) | `NitroSenseDevice.SetFanSpeeds`; `nitropower-cli fan` | works |
| `battery_limiter` (charge up to 80%) | `nitropower-cli battery-limit` | works |
| `battery_calibration` | `nitropower-cli battery-calibration` | works |
| `usb_charging` (0, 10, 20, 30%) | `nitropower-cli usb-charging` | works |
| `backlight_timeout` (keyboard backlight off after 30 s) | `nitropower-cli keyboard-timeout` | works |
| `lcd_override` (LCD overdrive) | `nitropower-cli lcd-overdrive` | the firmware returns `0xFF000001000000`, outside the expected values: probably unsupported |
| `boot_animation_sound` | `nitropower-cli boot-sound` | the firmware returns status 1: probably unsupported |
| `four_zoned_kb/four_zone_mode`: static, breathing, neon, wave, shifting, zoom, meteor and twinkling effects (speed, brightness, direction, color) | `FourZoneKeyboard`, `KeyboardEffectState` (the driver's validation rules); `nitropower-cli keyboard effect` | absent: with `four_zone_kb=1` the effect reads all zeros |
| `four_zoned_kb/per_zone_mode`: color per zone + brightness | `PerZoneColors`; `nitropower-cli keyboard zones` | absent: the firmware returns status 1 (error) for all four zones |
| State per power source (profile and fans on AC and on battery, in `/etc/predator_state`) | stays in the driver (WMI AC event) | works in the driver |
| Keyboard state in `/etc/four_zone_kb_state` | stays in the driver | — |
| hwmon (temperatures, fan RPM) | `Hwmon`, `SensorReader`; `probe` | works |
| HID RGB controller (ENEK5130, `0CF2:5130`) | not ported: this model has no such device | absent |

## User-space tools

| Linuwu-Sense tool | In NitroPower |
| --- | --- |
| `Linuwu-Sense.py --status` and the settings | `nitropower-cli status` and the per-feature commands |
| `Linuwu-Sense.py --save/--load/--list-profiles` (named profiles) | `nitropower-cli preset save/load/list/show/delete` in `/etc/nitropower/presets`. It also stores the mode, turbo and the overclock |
| `profile-notifier.py` (notification on profile change) | `nitropower-cli watch --notify`. It uses `poll()` on `platform_profile` instead of re-reading the file, because every read is an SMI call into the firmware |
| `osd/linuwu_turbo_osd.py` (turbo notification) | the same `watch --notify`: it reports turbo on and off, from NitroPower and from the firmware LED, and Nitro key failures (e.g. turbo on battery). It starts with the session (`/etc/xdg/autostart/nitropower-notify.desktop`) |
| `osd/penguin_sense.py` (sensor monitor) | the Dashboard page of the interface; `nitropower-cli probe` on the command line |

## Patch 0002: forcing turbo and the RGB keyboard

Two module parameters, for models missing from the quirk table (or to probe the
hardware):

| Parameter | Values | Effect |
| --- | --- | --- |
| `turbo_sense` | -1 (model default), 0, 1 | `turbo` quirk: the `turbo_sense` group and turbo toggling from the WMI key |
| `four_zone_kb` | -1, 0, 1 | `four_zone_kb` quirk: the `four_zoned_kb` group |

To make it permanent: `options linuwu_sense four_zone_kb=1` in
`/etc/modprobe.d/`. NitroPower detects the groups through sysfs, so nothing
changes in the application.

## ANV15-52 probe (BIOS V1.60, 2026-11-16)

The driver was loaded with `four_zone_kb=1 turbo_sense=1` and the files were
only read:

| File | Response | Conclusion |
| --- | --- | --- |
| `four_zoned_kb/four_zone_mode` | `0,0,0,0,0,0,0` | no RGB effects |
| `four_zoned_kb/per_zone_mode` | `1000000` for each zone (raw response 1 = error) and brightness 0 | no per-zone color |
| `turbo_sense/turbo_state` | `0` | no turbo LED |

The profile marks `rgbKeyboard` and `turboLed` as `none`. The
`keyboard effect`/`keyboard zones` commands remain in NitroPower for models
that have the feature.

## NitroPower state and files

| File | Contents |
| --- | --- |
| `/var/lib/nitropower/state.json` | active turbo and what to restore, last mode outside turbo, overclock turned off by the user |
| `/etc/nitropower/presets/*.json` | named presets |
