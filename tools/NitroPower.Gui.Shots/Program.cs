using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using NitroPower.Gui;
using NitroPower.Gui.Services;
using NitroPower.Gui.ViewModels;
using NitroPower.Gui.Views;

namespace NitroPower.Gui.Shots;

public static class Entry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders each page of the demo to PNG: &lt;dir&gt;/&lt;lang&gt;-&lt;theme&gt;-&lt;page&gt;.png.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var dir = Path.GetFullPath(args.Length > 0 ? args[0] : "shots");
        var height = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1500;
        var only = args.Length > 2 && args[2] != "all" ? args[2].Split(',') : null;
        var width = args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1220;
        Directory.CreateDirectory(dir);
        using var session = HeadlessUnitTestSession.StartNew(typeof(Entry));
        session.Dispatch(() => Run(dir, width, height, only), CancellationToken.None).GetAwaiter().GetResult();
        return 0;
    }

    private static async Task<bool> Run(string dir, int width, int height, string[]? only)
    {
        var nitro = new NitroSession("/nonexistent", demo: true);
        var vm = new MainViewModel(nitro, Path.Join(dir, "gui-settings.json"));
        vm.UpdateSettings(s => s with { Language = "pt", Theme = "dark" });
        vm.ApplyTheme();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        nitro.Start();
        await nitro.RefreshStatus();
        for (var i = 0; i < 120; i++)
        {
            var s = nitro.Demo!.Sensors();
            foreach (var page in vm.Pages)
                page.OnSensors(s);
        }
        window.Show();
        nitro.SensorsEnabled = false;

        foreach (var (lang, theme) in new[] { ("pt", "dark"), ("en", "dark"), ("pt", "light") })
        {
            vm.UpdateSettings(s => s with { Language = lang, Theme = theme });
            foreach (var page in vm.Pages)
            {
                if (only is not null && !only.Contains(page.Key))
                    continue;
                if (theme == "light" && page.Key is not ("dashboard" or "fans" or "performance"))
                    continue;
                vm.CurrentPage = page;
                await Settle();
                Save(window, Path.Join(dir, $"{lang}-{theme}-{page.Key}.png"));
            }
        }
        window.Close();
        return true;
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(40);
        }
    }

    private static void Save(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine(path);
    }
}
