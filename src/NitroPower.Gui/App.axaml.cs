using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NitroPower.Gui.Services;
using NitroPower.Gui.ViewModels;
using NitroPower.Gui.Views;

namespace NitroPower.Gui;

public sealed class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private TrayController? _tray;
    private SingleInstance? _instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var options = Program.Options;
            var session = new NitroSession(options.Socket, options.Demo);
            _vm = new MainViewModel(session, AppSettings.DefaultPath);
            _vm.ApplyTheme();
            if (options.Page is { } page)
                _vm.ShowPage(page);

            _window = new MainWindow { DataContext = _vm };
            if (options.Demo)
                _window.Title = "NitroPower (demo)";
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Cleanup();
            _window.Closed += (_, _) =>
            {
                if (_window.AllowClose || !_window.HasTray)
                    Quit();
            };

            session.ShowRequested += ShowWindow;
            if (!options.Demo)
                _instance = SingleInstance.Claim(command => Dispatcher.UIThread.Post(() => OnCommand(command)));

            session.Start();
            if (options.Hidden)
                session.SensorsEnabled = false;
            else
                _window.Show();
            _ = SetUpTray(options.Hidden);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task SetUpTray(bool hidden)
    {
        var supported = await TrayController.IsSupportedAsync();
        if (supported && _vm is not null)
        {
            _tray = new TrayController(this, _vm, ShowWindow, Quit);
            if (_window is not null)
                _window.HasTray = true;
        }
        else if (hidden)
        {
            ShowWindow(); // nowhere to hide: show the window after all
        }
    }

    private void OnCommand(string command)
    {
        var parts = command.Split(' ', 2);
        if (parts[0] == "show")
        {
            if (parts.Length > 1)
                _vm?.ShowPage(parts[1]);
            ShowWindow();
        }
        else if (parts[0] == "quit")
        {
            Quit();
        }
    }

    private void ShowWindow() => _window?.Present();

    private void Quit()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            if (_window.IsVisible)
                _window.Close();
        }
        _desktop?.Shutdown();
    }

    private void Cleanup()
    {
        _tray?.Dispose();
        _tray = null;
        _instance?.Dispose();
        _instance = null;
        _vm?.Session.Dispose();
    }
}
