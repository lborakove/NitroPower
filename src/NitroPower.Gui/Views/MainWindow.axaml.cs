using Avalonia;
using Avalonia.Controls;
using NitroPower.Gui.ViewModels;

namespace NitroPower.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Set when the window may really close (quit, or no tray to hide in).</summary>
    public bool AllowClose { get; set; }

    /// <summary>Whether a tray icon can hold NitroPower while the window is closed.</summary>
    public bool HasTray { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && HasTray && DataContext is MainViewModel { Settings.CloseToTray: true })
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IsVisibleProperty || change.Property == WindowStateProperty) &&
            DataContext is MainViewModel vm)
            vm.Session.SensorsEnabled = IsVisible && WindowState != WindowState.Minimized;
    }

    /// <summary>Shows the window, restored and in front.</summary>
    public void Present()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }
}
