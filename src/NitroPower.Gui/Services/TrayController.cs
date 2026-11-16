using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using NitroPower.Gui.Localization;
using NitroPower.Gui.ViewModels;

namespace NitroPower.Gui.Services;

/// <summary>The tray icon: modes, turbo and fans one click away, and the window.</summary>
public sealed class TrayController : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Action _show;
    private readonly Action _quit;
    private readonly TrayIcon _icon;

    public TrayController(Application app, MainViewModel vm, Action show, Action quit)
    {
        _vm = vm;
        _show = show;
        _quit = quit;
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://nitropower/Assets/nitropower-64.png"))),
            ToolTipText = "NitroPower",
        };
        _icon.Clicked += (_, _) => _show();
        Rebuild();
        TrayIcon.SetIcons(app, [_icon]);
        vm.Session.PropertyChanged += OnSession;
        Loc.Instance.Changed += Rebuild;
    }

    /// <summary>True when a StatusNotifierItem host (GNOME AppIndicator, KDE) runs.</summary>
    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gdbus",
                "call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus " +
                "--method org.freedesktop.DBus.NameHasOwner org.kde.StatusNotifierWatcher")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return output.Contains("true", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return true; // no gdbus: assume a tray, as most desktops have one
        }
    }

    private void OnSession(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NitroSession.Status) or nameof(NitroSession.Link))
            Rebuild();
    }

    private void Rebuild()
    {
        var s = _vm.Session.Status;
        var menu = new NativeMenu();
        var open = new NativeMenuItem(Loc.T("tray.open"));
        open.Click += (_, _) => _show();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());

        if (s is not null && _vm.Session.IsConnected)
        {
            foreach (var mode in s.Modes)
            {
                var id = mode.Id;
                var item = new NativeMenuItem(Visuals.ModeName(id))
                {
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = s.Mode == id && !s.Turbo,
                    IsEnabled = s.Power.OnAc || !mode.RequiresAc,
                };
                item.Click += async (_, _) => await _vm.SetMode(id);
                menu.Add(item);
            }
            var turbo = new NativeMenuItem("Turbo")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = s.Turbo,
                IsEnabled = s.Power.OnAc || s.Turbo,
            };
            turbo.Click += async (_, _) => await _vm.ToggleTurbo();
            menu.Add(turbo);
            menu.Add(new NativeMenuItemSeparator());

            var fans = new NativeMenu();
            foreach (var choice in new[] { "auto", "max", "curve" })
            {
                var item = new NativeMenuItem(Visuals.FanChoice(choice))
                {
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = s.FanChoice == choice,
                };
                item.Click += async (_, _) => await _vm.SetFans(choice);
                fans.Add(item);
            }
            menu.Add(new NativeMenuItem(Loc.F("tray.fans", Visuals.FanChoice(s.FanChoice))) { Menu = fans });
            menu.Add(new NativeMenuItemSeparator());
        }
        else
        {
            menu.Add(new NativeMenuItem(_vm.LinkText) { IsEnabled = false });
            menu.Add(new NativeMenuItemSeparator());
        }

        var quit = new NativeMenuItem(Loc.T("tray.quit"));
        quit.Click += (_, _) => _quit();
        menu.Add(quit);
        _icon.Menu = menu;
        _icon.ToolTipText = s is null
            ? "NitroPower"
            : $"NitroPower — {(s.Turbo ? "Turbo" : Visuals.ModeName(s.Mode))} · {Visuals.FanChoice(s.FanChoice)}";
    }

    public void Dispose()
    {
        _vm.Session.PropertyChanged -= OnSession;
        Loc.Instance.Changed -= Rebuild;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
