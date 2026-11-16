using System.Globalization;
using System.Runtime.InteropServices;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Daemon;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

string? Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine($"""
        nitropowerd {NitroApiProtocol.SoftwareVersion} — NitroPower daemon (run by systemd as root)

        Options:
          --socket PATH     API socket ({NitroApiProtocol.DefaultSocket})
          --state FILE      state file ({StateStore.DefaultPath})
          --presets DIR     presets ({PresetStore.DefaultDirectory})
          --profiles DIR    extra model profile directory
          --no-nitro-key    do not listen to the Nitro key
          --verbose         debug messages
        """);
    return 0;
}

var options = new DaemonOptions(
    Socket: Value("--socket") ?? NitroApiProtocol.DefaultSocket,
    State: Value("--state") ?? StateStore.DefaultPath,
    Presets: Value("--presets") ?? PresetStore.DefaultDirectory,
    Profiles: Value("--profiles"),
    NitroKey: !args.Contains("--no-nitro-key"),
    Verbose: args.Contains("--verbose"));
Systemd.Verbose = options.Verbose;

using var stop = new CancellationTokenSource();
void OnSignal(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

try
{
    using var daemon = new Daemon(options);
    return daemon.Run(stop.Token);
}
catch (Exception e)
{
    Systemd.Error($"fatal: {e}");
    return 1;
}
