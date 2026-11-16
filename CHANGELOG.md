# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Repository structure and architecture.
- Profile for the Acer Nitro V 15 (ANV15-52), BIOS V1.31/V1.60.
- Guide to updating the BIOS from Linux with fwupd (`docs/BIOS.md`).
- `linuwu_sense` driver (upstream 4483b6c) with patch 0001: falls back to the
  WMI interface when `EC0._Q30` does not exist, and exposes Acer's Performance
  mode as `performance` when the firmware has no turbo.
- `NitroPower.Core` (.NET 10): model profiles with validation and DMI
  identification; `platform_profile`; `nitro_sense` features (fans, battery,
  USB charging, keyboard backlight, LCD overdrive, boot sound); power and PCI;
  NVML (clock offsets and telemetry, without waking a suspended GPU); sensors
  (Acer EC, coretemp, NVMe, CPU load and clocks); overclock policy with battery
  hysteresis; fan curve with a hysteresis controller.
- `nitropower-cli probe`: read-only hardware diagnostics.
- Parity with Linuwu-Sense ([docs/LINUWU-SENSE.md](docs/LINUWU-SENSE.md)):
  - turbo (top mode + fans at maximum + that mode's overclock), restoring the
    previous mode and fan settings;
  - mode key in user space (separate cycles on AC and on battery; quiet and eco
    return the fans to automatic) and the `cycle_gaming_thermal_profile`
    parameter;
  - 4-zone RGB keyboard: 8 effects and per-zone color, with the driver's
    validation rules; `turbo_sense/turbo_state`;
  - named presets (`/etc/nitropower/presets`) and persistent state
    (`/var/lib/nitropower/state.json`);
  - `watch --notify`: mode and turbo notifications, using `poll()` on sysfs.
- `nitropower-cli`: commands for every feature (`status`, `mode`, `turbo`,
  `fan`, `gpu-oc`, `mode-key`, `battery-limit`, `battery-calibration`,
  `usb-charging`, `keyboard`, `keyboard-timeout`, `lcd-overdrive`,
  `boot-sound`, `preset`, `watch`). Profiles are copied next to the executable.
- Profile: optional `modeCycle` and `turbo` fields, filled in for the ANV15-52.
- Driver patch 0002: `turbo_sense` and `four_zone_kb` parameters to enable
  those features on models missing from the quirk table.
- `nitropowerd` (systemd, `Type=notify`): JSON-RPC 2.0 API over a Unix socket
  (`root:nitropower` 0660, events), Nitro key through evdev (`turbo`, `cycle`,
  `gui` or `none` action), GPU overclock replacing `nitro-gpu-oc` (persistent
  battery hysteresis, re-applied after a reset), software fan curve, and turbo
  ends when another program changes the mode.
- `INitroApi` with local execution and a client (`NitroClient`); the CLI uses
  the daemon when it is running (no sudo for the `nitropower` group) and falls
  back to direct access. New commands: `fan curve`, `nitro-key`, `--direct`.
- Persistent fan choice (`auto`, `max`, manual, `curve`); turbo restores the
  curve. Presets store the curve.
- `watch --notify` uses the daemon's events and reports Nitro key failures.
- `packaging/`: `build.sh`, `install-dev.sh` (installs and uninstalls; disables
  `nitro-gpu-oc` and the GNOME shortcut), systemd unit and notification
  autostart.
- `nitropower` graphical interface (Avalonia 12, phase 3), a daemon client:
  - Dashboard with gauges and 2-minute charts (temperatures, fans, load), quick
    control of mode, turbo and fans;
  - Performance: modes, turbo, mode key cycle, GPU overclock with editable
    levels per mode;
  - Fans: automatic, maximum, manual and a graphical curve editor (CPU and
    GPU, ready-made templates, current temperature marker);
  - Battery (charge, power, time, health, cycles, limit, calibration, USB
    charging), Keyboard & display (backlight, overdrive, boot sound, 4-zone RGB
    with preview), Nitro key, Presets, System (driver, parameters, service
    logs);
  - system tray, one instance per user (`show` over a socket), `--hidden`,
    `--page`, `--demo`; English and Brazilian Portuguese; dark and light
    themes; start with the session; the Nitro key's `gui` action opens the
    window.
- API: `gpu.levels.set` and `gpu.levels.reset` (user overclock levels, kept in
  the state); `status.get` includes `modeCycle`, each mode's `gpuOcLevel`, and
  the overclock levels and policy. CLI: `gpu-oc levels`.
- `tools/NitroPower.Gui.Shots`: headless page screenshots; `--docs` rewrites
  the documentation screenshots.
- `install-dev.sh` installs the interface, its launcher and icons;
  `--replace-damx` also hides the DAMX launcher. `watch --notify` opens the
  window on the Nitro key when it is not already open.
- 164 tests: 127 for Core and 37 for the interface (strings in both
  languages, battery, and headless interaction tests: modes, turbo, curve,
  presets, language, a sweep that clicks every option, switch and list on
  every page, and grid layout at four window widths).

### Changed

- Documentation, screenshots and repository description are in English.
  `docs/ARQUITETURA.md` is now `docs/ARCHITECTURE.md`.

### Security

- `install-dev.sh` installed the daemon files owned by the build user
  (`cp -a`), so that user could replace code that runs as root. They are now
  owned by root, with no group or other write access.
- API: requests are limited to 64 KiB (an endless line used to fill the
  daemon's memory), at most 32 connections, and at most 100 presets.
- `gpu.offsets.set` was removed from the API (wide range, and the daemon undid
  it within seconds); `gpu-oc <core> <mem>` works only as root without the
  daemon.
- Nitro key: `EVIOCSMASK` filter, so the daemon does not receive other keys.
- Journal: messages with line breaks can no longer forge other entries.
- systemd: `PrivateNetwork`, `RestrictAddressFamilies`, `ProtectKernelModules`,
  `ProtectKernelLogs`, `ProtectControlGroups`, `ProtectHostname`,
  `ProtectProc`, `RestrictNamespaces`, `RestrictRealtime`, `RestrictSUIDSGID`,
  `LockPersonality`, `SystemCallArchitectures` (exposure 8.7 → 5.4).
- `--replace-damx` removes the `linuwu_sense` group's direct write access to
  sysfs.
- Interface: the single-instance socket lives outside the shared `/tmp`;
  `notify-send` receives the text after `--`.

### Fixed

- `driver/linuwu_sense/dkms-sign.sh` was missing (`dkms.conf` calls it).
- The profile pointed to a nonexistent `profile.schema.json`.
- Removed unused members and divider comments.
- Interface: the language did not switch back to English after choosing
  Portuguese. The language, theme, interval and RGB effect lists were rebuilt
  on every language change, and the ComboBox restored the previous choice.
- Interface: "Same speed for both" did not stay unchecked when both fans had
  the same speed, and a status update could undo a manual speed change that
  had not been applied yet.
- Interface: "Manual" on the dashboard opened the Fans page without selecting
  manual mode.
- Interface: the display overdrive switch was clickable when the firmware does
  not report its state, and switched itself back off.
- Interface: the card in the last column of a grid (Performance, in wide
  windows) lost its right border. Avalonia's UniformGrid could push the last
  column up to 2 px past the edge; the new `EvenGrid` panel always fits.
