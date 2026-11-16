using System.Globalization;
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

/// <summary>
/// Renders the demo to PNG. Two modes:
/// <c>[dir] [height] [pages|all] [width]</c> writes every page as &lt;dir&gt;/&lt;lang&gt;-&lt;theme&gt;-&lt;page&gt;.png;
/// <c>--docs [dir]</c> rewrites the English screenshots used by the documentation (default docs/screenshots).
/// </summary>
internal static class Program
{
    private const int DocsWidth = 1220;
    private const int DocsHeight = 800;

    /// <summary>The documentation screenshots: theme, page.</summary>
    private static readonly (string Theme, string Page)[] DocsShots =
    [
        ("dark", "dashboard"), ("dark", "performance"), ("dark", "fans"), ("dark", "battery"),
        ("dark", "device"), ("dark", "nitrokey"), ("light", "dashboard"),
    ];

    private static int Main(string[] args)
    {
        var docs = args.Length > 0 && args[0] == "--docs";
        if (docs)
            args = args[1..];
        var dir = Path.GetFullPath(args.Length > 0 ? args[0] : docs ? Path.Join("docs", "screenshots") : "shots");
        var height = docs ? DocsHeight : args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1500;
        var width = docs ? DocsWidth : args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1220;
        var only = !docs && args.Length > 2 && args[2] != "all" ? args[2].Split(',') : null;
        var shots = docs
            ? DocsShots.Select(s => ("en", s.Theme, s.Page, $"{s.Theme}-{s.Page}.png")).ToList()
            : (from combo in new[] { ("pt", "dark"), ("en", "dark"), ("pt", "light"), ("en", "light") }
               from page in new[] { "dashboard", "performance", "fans", "battery", "device", "nitrokey", "presets", "system" }
               where only is null || only.Contains(page)
               where combo.Item2 == "dark" || page is "dashboard" or "fans" or "performance"
               select (combo.Item1, combo.Item2, page, $"{combo.Item1}-{combo.Item2}-{page}.png")).ToList();

        Directory.CreateDirectory(dir);
        using var session = HeadlessUnitTestSession.StartNew(typeof(Entry));
        session.Dispatch(() => Run(dir, width, height, shots), CancellationToken.None).GetAwaiter().GetResult();
        return 0;
    }

    private static async Task<bool> Run(string dir, int width, int height,
        List<(string Lang, string Theme, string Page, string File)> shots)
    {
        var settings = Directory.CreateTempSubdirectory("nitropower-shots-");
        var nitro = new NitroSession("/nonexistent", demo: true);
        var vm = new MainViewModel(nitro, Path.Join(settings.FullName, "gui-settings.json"));
        vm.UpdateSettings(s => s with { Language = shots[0].Lang, Theme = shots[0].Theme });
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

        foreach (var shot in shots)
        {
            vm.UpdateSettings(s => s with { Language = shot.Lang, Theme = shot.Theme });
            vm.CurrentPage = vm.Pages.First(p => p.Key == shot.Page);
            await Settle();
            Save(window, Path.Join(dir, shot.File));
        }
        window.Close();
        settings.Delete(recursive: true);
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
