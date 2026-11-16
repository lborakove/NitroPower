using Avalonia;
using NitroPower.Core.Api;
using NitroPower.Gui.Services;

namespace NitroPower.Gui;

public sealed record GuiOptions(bool Demo, bool Hidden, string Socket, string? Page);

internal static class Program
{
    public static GuiOptions Options { get; private set; } = new(false, false, NitroApiProtocol.DefaultSocket, null);

    [STAThread]
    public static int Main(string[] args)
    {
        var demo = false;
        var hidden = false;
        var socket = NitroApiProtocol.DefaultSocket;
        string? page = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    demo = true;
                    break;
                case "--hidden" or "--tray":
                    hidden = true;
                    break;
                case "--socket" when i + 1 < args.Length:
                    socket = args[++i];
                    break;
                case "--page" when i + 1 < args.Length:
                    page = args[++i];
                    break;
                case "--version":
                    Console.WriteLine($"nitropower {NitroApiProtocol.SoftwareVersion}");
                    return 0;
                case "-h" or "--help":
                    Console.WriteLine("""
                        Usage: nitropower [--hidden] [--page NAME] [--demo] [--socket PATH]

                          --hidden       start in the tray, without the window
                          --page NAME    open on a page: dashboard, performance, fans, battery,
                                         device, nitrokey, presets, system
                          --demo         pretend hardware, to try the interface
                          --socket PATH  nitropowerd socket (default /run/nitropower/nitropower.sock)
                        """);
                    return 0;
                default:
                    Console.Error.WriteLine($"nitropower: unknown option '{args[i]}' (see --help)");
                    return 2;
            }
        }
        Options = new GuiOptions(demo, hidden, socket, page);

        // One window per user: a second launch brings the first one forward
        if (!demo && SingleInstance.Signal(page is null ? "show" : $"show {page}"))
            return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Matches StartupWMClass in nitropower.desktop, so the dock shows our icon
            .With(new X11PlatformOptions { WmClass = "nitropower" })
            .WithInterFont()
            .LogToTrace();
}
