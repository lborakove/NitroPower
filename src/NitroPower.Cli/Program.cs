using System.Globalization;
using NitroPower.Cli;
using NitroPower.Core.Api;
using NitroPower.Core.Control;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

try
{
    var a = new Args(args);
    var profilesDir = a.Option("--profiles");
    var statePath = a.Option("--state") ?? StateStore.DefaultPath;
    var presetsDir = a.Option("--presets") ?? PresetStore.DefaultDirectory;
    var socketPath = a.Option("--socket") ?? NitroApiProtocol.DefaultSocket;
    var command = a.Next() ?? "help";
    if (a.HasFlag("--help") || a.HasFlag("-h"))
        command = "help";

    using var session = new CliSession(profilesDir, statePath, presetsDir, socketPath, a.HasFlag("--direct"));
    switch (command)
    {
        case "help":
            Console.WriteLine(Usage());
            return 0;
        case "version" or "--version":
            Console.WriteLine($"nitropower-cli {NitroApiProtocol.SoftwareVersion} (API {NitroApiProtocol.Version})");
            if (session.UsesDaemon && session.Api.Info() is { } info)
                Console.WriteLine($"nitropowerd    {info.Version} (API {info.Protocol})");
            return 0;
    }

    return command switch
    {
        "probe" => ProbeCommand.Run(session, a),
        "status" => ControlCommands.Status(session, a),
        "watch" => WatchCommand.Run(session, a),
        "mode" => ControlCommands.Mode(session, a),
        "turbo" => ControlCommands.Turbo(session, a),
        "fan" or "fans" => ControlCommands.Fan(session, a),
        "gpu-oc" => ControlCommands.GpuOc(session, a),
        "nitro-key" => ControlCommands.NitroKey(session, a),
        "mode-key" => ControlCommands.ModeKey(session, a),
        "battery-limit" => ControlCommands.Feature(session, a, NitroFeatures.BatteryLimiter),
        "battery-calibration" => ControlCommands.Feature(session, a, NitroFeatures.BatteryCalibration),
        "usb-charging" => ControlCommands.UsbCharging(session, a),
        "keyboard" => ControlCommands.Keyboard(session, a),
        "keyboard-timeout" => ControlCommands.Feature(session, a, NitroFeatures.KeyboardTimeout),
        "lcd-overdrive" => ControlCommands.Feature(session, a, NitroFeatures.LcdOverdrive),
        "boot-sound" => ControlCommands.Feature(session, a, NitroFeatures.BootSound),
        "preset" or "presets" => ControlCommands.Preset(session, a),
        _ => throw new UsageException($"unknown command '{command}'"),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"nitropower-cli: {e.Message} (see nitropower-cli help)");
    return 2;
}
catch (NitroException e)
{
    Console.Error.WriteLine($"nitropower-cli: {e.Message}");
    return 1;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("nitropower-cli: permission denied: run it with sudo");
    return 1;
}
catch (IOException e)
{
    // sysfs writes the driver refuses end up here (EINVAL, ENODEV, EOPNOTSUPP)
    Console.Error.WriteLine($"nitropower-cli: the driver refused the change: {e.Message}");
    return 1;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"nitropower-cli: {e.Message}");
    return 2;
}

static string Usage() => $"""
    nitropower-cli {NitroApiProtocol.SoftwareVersion} — Acer Nitro/Predator control (linuwu_sense + NVML)

    With nitropowerd running, members of the '{NitroApiProtocol.Group}' group need no sudo.
    Without it, commands act on the hardware directly and changes need sudo.

    Information:
      probe                               full hardware report
      status                              every setting at a glance
      watch [--notify]                    follow mode, turbo and Nitro key events; --notify
                                          shows desktop notifications (run in your session)

    Performance:
      mode                                show the modes
      mode <id>                           select: eco quiet balanced performance (turbo)
      mode next                           next mode in the cycle (as the mode key)
      turbo [on|off|toggle]               top mode + fans at maximum; off restores the
                                          previous mode and fans
      fan [auto|max|<cpu%> <gpu%>]        fan duty 1..100 (0 = automatic for that fan)
      fan curve [on|reset]                software fan curve (run by nitropowerd)
      fan curve set cpu|gpu|both <t:%,...>  e.g. 45:25,60:40,75:65,88:100
      gpu-oc [apply|on|off|reset]         GPU overclock that follows the mode
      gpu-oc <core> <mem>                 manual offsets in MHz, without nitropowerd (root)
      gpu-oc levels [reset|core/mem ...]  offsets per overclock level (one per level)
      nitro-key [turbo|cycle|gui|none]    what the Nitro key does (nitropowerd)
      nitro-key press                     run the Nitro key action now
      mode-key [cycle|turbo]              firmware mode key (models with WMI key event)

    Battery and power:
      battery-limit [on|off]              stop charging at 80%
      battery-calibration [start|stop]    full charge/discharge cycle; keep AC connected
      usb-charging [off|10|20|30]         USB power while off, until the battery reaches N%

    Keyboard and display:
      keyboard                            show the RGB lighting
      keyboard effect <name> [--color RRGGBB] [--speed 0-9] [--brightness 0-100]
                             [--direction rtl|ltr]
          effects: static breathing neon wave shifting zoom meteor twinkling
      keyboard zones <RRGGBB> [<RRGGBB> <RRGGBB> <RRGGBB>] [--brightness 0-100]
      keyboard-timeout [on|off]           keyboard light off after 30 s idle
      lcd-overdrive [on|off]              faster LCD response
      boot-sound [on|off]                 boot animation and sound

    Presets:
      preset [list] | show <name>         saved presets
      preset save|load|delete <name>      save or apply the current settings

    Options: --direct (skip nitropowerd)   --socket PATH ({NitroApiProtocol.DefaultSocket})
             --profiles DIR   --state FILE ({StateStore.DefaultPath})   --presets DIR
    Without an argument, a setting command shows the current value.
    """;
