using System.Diagnostics;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Monitoring;

namespace NitroPower.Cli;

/// <summary>
/// Follows mode and turbo changes from any source (Nitro key, NitroPower,
/// GNOME power menu, the driver on AC events) and optionally shows them as
/// desktop notifications that replace each other, like an OSD. Port of the
/// Linuwu-Sense profile notifier and turbo OSD. With nitropowerd it uses the
/// daemon's events (which also report Nitro key errors); without it, poll() on
/// sysfs. Needs no root.
/// </summary>
internal static class WatchCommand
{
    public static int Run(CliSession s, Args args)
    {
        var notify = args.HasFlag("--notify");
        args.End();
        var osd = new Osd(notify ? new DesktopNotifier() : null, Text.ForEnvironment());

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        if (s.NewEventClient() is { } events)
        {
            using (events)
            {
                var st = s.Api.Status();
                osd.Mode(st.Mode ?? st.PlatformProfile, st.Turbo, initial: true);
                events.Subscribe(osd.Event, stop.Token);
            }
            if (stop.IsCancellationRequested)
                return 0;
            Osd.Print("nitropowerd stopped; watching sysfs instead");
        }
        return WatchSysfs(s.Local.Controller, osd, stop.Token);
    }

    private static int WatchSysfs(NitroController c, Osd osd, CancellationToken ct)
    {
        var paths = new List<string> { PlatformProfile.CurrentPath };
        if (c.FirmwareTurbo is not null)
            paths.Add(TurboSense.StatePath);
        using var watcher = new AttributeWatcher(paths);
        if (watcher.Paths.Count == 0)
            throw new NitroException("nothing to watch: platform_profile is not available");

        string? ModeOf(string? platformProfile) =>
            c.Profile?.FindModeByPlatformProfile(platformProfile)?.Id ?? platformProfile;

        bool TurboFor(string? modeId) =>
            c.Profile is { } p && modeId == p.EffectiveTurbo.Mode && c.Store.Load().Turbo is not null;

        var initial = watcher.ReadAll().ToDictionary(x => x.Path, x => x.Value);
        var mode = ModeOf(initial.GetValueOrDefault(PlatformProfile.CurrentPath));
        osd.Mode(mode, TurboFor(mode), initial: true);

        while (!ct.IsCancellationRequested)
        {
            foreach (var change in watcher.Wait(1000))
            {
                if (change.Path == PlatformProfile.CurrentPath)
                {
                    mode = ModeOf(change.Value);
                    osd.Mode(mode, TurboFor(mode), initial: false);
                }
                else if (change.Path == TurboSense.StatePath)
                {
                    osd.FirmwareTurbo(change.Value == "1");
                }
            }
        }
        return 0;
    }
}

/// <summary>Turns state changes into console lines and notifications.</summary>
internal sealed class Osd(DesktopNotifier? notifier, Text text)
{
    private string? _mode;
    private bool? _turbo;

    public static void Print(string what) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {what}");

    public void Mode(string? mode, bool turbo, bool initial)
    {
        if (_turbo is not null && turbo != _turbo || initial && turbo)
        {
            Print(turbo ? $"turbo on (mode {mode})" : $"turbo off (mode {mode ?? "unknown"})");
            if (!initial)
                notifier?.Show(text.Turbo(turbo, turbo ? null : mode), Icon(mode ?? "", turbo));
        }
        else if (mode != _mode)
        {
            Print($"mode {mode ?? "unknown"}");
            if (!initial && mode is not null)
                notifier?.Show(text.Mode(mode), Icon(mode, false));
        }
        _mode = mode;
        _turbo = turbo;
    }

    public void FirmwareTurbo(bool on)
    {
        Print(on ? "firmware turbo on" : "firmware turbo off");
        notifier?.Show(text.Turbo(on, null), Icon("performance", on));
    }

    public void Event(NitroEvent e)
    {
        switch (e.Type)
        {
            case "mode":
                Mode(e.Mode, e.Turbo == true, initial: false);
                break;
            case "firmwareTurbo":
                FirmwareTurbo(e.Turbo == true);
                break;
            case "power":
                Print(e.OnAc == true ? $"AC connected ({e.BatteryPercent}%)" : $"on battery ({e.BatteryPercent}%)");
                break;
            case "nitroKey" when e.Message is not null:
                Print($"Nitro key: {e.Message}");
                notifier?.Show(text.NitroKeyFailed(e.Message), "dialog-warning-symbolic");
                break;
            case "nitroKey":
                Print($"Nitro key ({e.Action?.ToString().ToLowerInvariant()})");
                if (e.Action == NitroKeyAction.Gui && notifier is not null)
                    GuiLauncher.OpenIfClosed();
                break;
        }
    }

    /// <summary>Icon names from the freedesktop/GNOME icon theme.</summary>
    private static string Icon(string mode, bool turbo) => turbo
        ? "power-profile-performance-symbolic"
        : mode switch
        {
            "performance" or "turbo" => "power-profile-performance-symbolic",
            "eco" or "quiet" => "power-profile-power-saver-symbolic",
            _ => "power-profile-balanced-symbolic",
        };
}

/// <summary>
/// The Nitro key's "gui" action: a running NitroPower window reacts to the
/// event itself; when none runs, the session watcher starts one.
/// </summary>
internal static class GuiLauncher
{
    /// <summary>The GUI's single-instance socket (same rule as NitroPower.Gui SingleInstance).</summary>
    private static string SocketPath =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run
            ? Path.Join(run, $"nitropower-gui-{Environment.UserName}.sock")
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "nitropower", "gui.sock");

    public static void OpenIfClosed()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath));
            return; // running: it shows itself on the same event
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("nitropower") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Nitro key: the NitroPower window is not installed (nitropower not in PATH)");
        }
    }
}

/// <summary>Desktop notifications through notify-send; each one replaces the previous.</summary>
internal sealed class DesktopNotifier
{
    private string? _id;
    private bool _warned;

    public void Show(string body, string icon)
    {
        var psi = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--app-name=NitroPower", $"--icon={icon}", "--transient", "--print-id",
                     "--expire-time=2500", "--hint=string:x-canonical-private-synchronous:nitropower" })
            psi.ArgumentList.Add(a);
        if (_id is not null)
            psi.ArgumentList.Add($"--replace-id={_id}");
        psi.ArgumentList.Add("--"); // the body is text, never an option
        psi.ArgumentList.Add("NitroPower");
        psi.ArgumentList.Add(body);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            if (p.ExitCode == 0 && output.Length > 0)
                _id = output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!_warned)
                Console.Error.WriteLine("notify-send not found (package libnotify-bin); notifications disabled");
            _warned = true;
        }
    }
}

/// <summary>Notification texts in the user's language (Portuguese or English).</summary>
internal sealed class Text
{
    private readonly bool _pt;

    private Text(bool pt) => _pt = pt;

    public static Text ForEnvironment()
    {
        var lang = Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all ? all
            : Environment.GetEnvironmentVariable("LC_MESSAGES") is { Length: > 0 } msg ? msg
            : Environment.GetEnvironmentVariable("LANG") ?? "";
        return new Text(lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="returnedTo">Mode after turbo ended, shown with "off".</param>
    public string Turbo(bool on, string? returnedTo) =>
        (_pt ? (on ? "Turbo ligado" : "Turbo desligado") : (on ? "Turbo on" : "Turbo off")) +
        (returnedTo is null ? "" : " · " + Mode(returnedTo));

    public string NitroKeyFailed(string message) => (_pt ? "Tecla Nitro: " : "Nitro key: ") + message;

    public string Mode(string id) => _pt
        ? "Modo " + id switch
        {
            "eco" => "Eco",
            "quiet" => "Silencioso",
            "balanced" => "Equilibrado",
            "performance" => "Desempenho",
            "turbo" => "Turbo",
            _ => id,
        }
        : id switch
        {
            "eco" => "Eco",
            "quiet" => "Quiet",
            "balanced" => "Balanced",
            "performance" => "Performance",
            "turbo" => "Turbo",
            _ => id,
        } + " mode";
}
