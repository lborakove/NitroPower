# NitroPower architecture

A single `.deb` package that replaces DAMX, `nitro-gpu-oc` and the Nitro key
shortcut. License: GPL-3.0-or-later. The bundled driver is GPL-2.0-or-later.

## Components

```text
nitropower (.deb)
├── driver/linuwu_sense      kernel module (DKMS) + our own patches
├── nitropowerd              root daemon (systemd): the single source of truth
├── nitropower               graphical interface (Avalonia)
├── nitropower-cli           command line
└── /usr/share/nitropower/profiles/*.json   model profiles
```

### .NET projects (`src/`)

| Project | Role |
| --- | --- |
| `NitroPower.Core` | Model profiles, sysfs/hwmon access, NVML, policies and `Control/NitroController`, which carries out every feature (the same code for the CLI, the daemon and the interface) |
| `NitroPower.Daemon` | Root service: applies settings, overclock policy, fan curve, Nitro key, persistent state |
| `NitroPower.Cli` | Command line: an API client when the daemon is running; without it, it acts directly (as root) |
| `NitroPower.Gui` | Avalonia 12 interface (MVVM with CommunityToolkit.Mvvm), executable `nitropower` |
| `tests/NitroPower.Core.Tests` | Unit tests (profiles, protocol, policy) |
| `tests/NitroPower.Gui.Tests` | Strings, local readers and headless interaction tests |
| `tools/NitroPower.Gui.Shots` | Page screenshots (demo mode, no display needed); `--docs` rewrites `docs/screenshots/` |

### Control layer (`NitroPower.Core.Control`)

- `NitroController`: modes, mode key, turbo, fans, battery, USB charging,
  keyboard (timeout and RGB), LCD overdrive, boot sound, GPU overclock and the
  driver's mode key parameter.
- `StateStore`: `/var/lib/nitropower/state.json` (turbo and what to restore,
  last mode outside turbo, overclock turned off). It writes before touching the
  hardware and rolls back if the driver refuses.
- `Presets`: named presets in `/etc/nitropower/presets`.
- Turbo follows Linuwu-Sense: top mode + the profile's fan setting (`turbo` in
  the JSON); the GPU overclock comes from that mode's level.

Full mapping to Linuwu-Sense: [LINUWU-SENSE.md](LINUWU-SENSE.md).

### Daemon (`nitropowerd`)

Runs as root (systemd, `Type=notify`) and is the only writer to the hardware
while it is active. All hardware access goes through a single lock.

| Thread | Job |
| --- | --- |
| API | Unix socket, one task per client ([API.md](API.md)) |
| Watcher | `poll()` on `platform_profile` (and `turbo_state`) with a 5 s timeout: reports mode changes, ends turbo if another program changed the mode (and restores the fans), detects AC/battery and keeps the GPU overclock (replaces `nitro-gpu-oc`: does not wake the GPU, re-applies after a reset) |
| Fans | software curve every 2 s when the choice is `curve`; the CPU fan follows the CPU (coretemp) and the GPU fan follows the GPU (EC); writes only on change |
| Nitro key | reads `KEY_PROG1` from evdev on the profile's internal devices, without grabbing the keyboard; runs the configured action (`turbo`, `cycle`, `gui`, `none`) |

### Interface (`nitropower`)

- `Services/NitroSession`: the daemon connection. Calls run on worker threads,
  one at a time (`NitroClient` is not thread-safe); events arrive on a second
  connection; it reconnects every 3 s and reads sensors only while the window
  is visible. `DemoNitroApi` simulates a Nitro with RGB for `--demo`, tests and
  screenshots.
- `ViewModels/`: one per page. Status arrives with `Syncing` set, so setters do
  not send the value back to the daemon; edits in progress (curve, overclock
  levels) are reloaded only when the saved value changes.
- `Controls/`: hand-drawn: `ArcGauge`, `HistoryChart`, `FanCurveEditor`
  (dragging keeps the curve valid: rising temperature, power never drops),
  `KeyboardPreview`, `Icon` (our own line icons in `Styles/Icons.axaml`), and
  `EvenGrid`, an equal-cell grid used instead of Avalonia's UniformGrid, which
  can push the last column past its edge.
- `Localization/`: `Strings` (English and Brazilian Portuguese) and
  `{l:Tr key}` in XAML, which switches language with the window open.
- Tray (StatusNotifierItem), one instance per user
  (`$XDG_RUNTIME_DIR/nitropower-gui-$USER.sock`), preferences in
  `~/.config/nitropower/gui.json`, start with the session through
  `~/.config/autostart/nitropower.desktop`.

### Daemon API

- Unix socket at `/run/nitropower/nitropower.sock`, owned by
  `root:nitropower`, mode 0660. Only members of the `nitropower` group control
  the hardware, without sudo.
- JSON-RPC 2.0, one message per line; `system.version` reports the protocol
  version. Events for clients: mode, power, Nitro key, changes.
- `INitroApi` is the same interface for the client (socket) and for local
  execution, so the CLI and the interface cannot tell the difference.
- Reference: [API.md](API.md).

### Model profiles

Each model is a JSON file in `profiles/`, identified by DMI and by the GPU's
PCI IDs. The profile declares the modes and firmware values, overclock levels
per GPU, the available features and the Nitro key code. Supporting a new model
only takes a new file, with no code changes. A feature marked `"unknown"` is
probed at run time. Optional fields: `modeCycle` (mode key order on AC and on
battery, and which modes return the fans to automatic) and `turbo` (turbo mode
and fans).

## Security

- **Who controls the hardware.** Root and members of the `nitropower` group,
  through the `0660` socket. Group membership amounts to administering the
  hardware: changing modes, fans (down to 1%) and overclock within the
  profile's limits. It grants no access to other files or to system commands.
- **API input.** Every method validates its parameters (the profile's modes
  and features, a safe curve, overclock levels of core -300..300 and memory
  -1000..1500 MHz, preset names without `/`). Requests are at most 64 KiB, the
  server accepts up to 32 connections, and each client has 2 s to read an
  event. There are at most 100 presets. Manual GPU offsets do not exist in the
  API: only the CLI as root, without the daemon.
- **Nitro key.** The daemon opens the internal keyboard through evdev, but asks
  the kernel (`EVIOCSMASK`) for the Nitro key only: nothing else that is typed
  reaches the process. If the kernel does not accept the filter, the daemon
  logs it.
- **systemd sandbox.** Read-only file system (except state, configuration and
  socket), no network (`PrivateNetwork`, only `AF_UNIX`/`AF_NETLINK`), no module
  loading, no namespaces, no SUID. `/sys` and `/dev` stay writable because they
  are the daemon's job.
- **Logs.** Each message becomes one journal line (line breaks are replaced),
  so outside text cannot forge other entries.
- **Installation.** The files in `/usr/lib/nitropower` are owned by root and
  not writable by group or others: the daemon runs as root from them.
  `--replace-damx` removes the DAMX rule that gave the `linuwu_sense` group
  direct write access to sysfs, so the daemon is the only writer.
- **Driver.** The source in `/usr/src/linuwu_sense-*` is built as root by DKMS
  for every new kernel: it must be owned by root (`chown -R root:root`).

## Versioning

- SemVer: `MAJOR.MINOR.PATCH`. The version lives in `Directory.Build.props` and
  in `debian/changelog`.
- Separate versions for the API protocol and for the profile schema
  (`schemaVersion`).
- The driver records its upstream base in `driver/UPSTREAM` and its changes as
  numbered patches in `driver/patches/`.

## Installation and replacing the legacy tools

In the package's `postinst`:

1. Disable and remove `damx-daemon`, `nitro-gpu-oc` and the
   `nitro-key-detection` launcher, if present.
2. Remove the GNOME `Launch1` → `nitro-key-launch` shortcut. The daemon now
   handles the Nitro key.
3. Register the module with DKMS (`linuwu_sense`), build it and run
   `update-initramfs -u`. dracut copies the module into the initramfs.
4. Create the `nitropower` group, add the user and start `nitropowerd`.

## Roadmap

| Phase | Deliverable |
| --- | --- |
| 0 | Repository, documentation, ANV15-52 profile, driver with patches |
| 1 | `NitroPower.Core`: model detection, reading and writing features, NVML, monitoring |
| 1.5 | Linuwu-Sense parity: turbo, mode key, 4-zone RGB, presets, notifications; `nitropower-cli` does everything; patch 0002 |
| 2 | `nitropowerd` + `nitropower-cli`: API, overclock policy, fan curve, Nitro key, notifications; development install (`packaging/`) |
| 3 | Avalonia interface: dashboard, performance, fans, battery, display/keyboard, Nitro key, presets, system, tray (done) |
| 4 | `.deb` package with DKMS, systemd, polkit/group and legacy replacement |
| 5 | Probing the untested features: firmware fan table, boot logo, Windows key lock, RGB, MUX |
