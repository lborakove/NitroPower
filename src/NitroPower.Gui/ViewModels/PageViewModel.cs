using CommunityToolkit.Mvvm.ComponentModel;
using NitroPower.Core.Api;
using NitroPower.Core.Monitoring;
using NitroPower.Gui.Localization;
using NitroPower.Gui.Services;

namespace NitroPower.Gui.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Re-reads every bound property (language change).</summary>
    public void RefreshAll() => OnPropertyChanged(string.Empty);
}

/// <summary>A page of the main window. Status updates run with
/// <see cref="Syncing"/> set, so property setters do not send them back to the daemon.</summary>
public abstract class PageViewModel(MainViewModel main) : ViewModelBase
{
    public MainViewModel Main => main;
    public NitroSession Session => main.Session;

    /// <summary>"dashboard", "performance"...: names the texts nav.{Key} and nav.{Key}.sub.</summary>
    public abstract string Key { get; }

    public abstract string IconKey { get; }

    public Avalonia.Media.Geometry? Icon => Visuals.Icon(IconKey);
    public string Title => Loc.T($"nav.{Key}");
    public string Subtitle => Loc.T($"nav.{Key}.sub");

    protected bool Syncing { get; private set; }

    public NitroStatus? Status => Session.Status;

    public void Sync(NitroStatus status)
    {
        Syncing = true;
        try
        {
            OnStatus(status);
        }
        finally
        {
            Syncing = false;
        }
    }

    protected virtual void OnStatus(NitroStatus status)
    {
    }

    public virtual void OnSensors(SensorSnapshot sensors)
    {
    }

    /// <summary>The page became visible.</summary>
    public virtual void OnShown()
    {
    }

    public virtual void OnLanguageChanged()
    {
        RefreshAll();
        if (Session.Status is { } s)
            Sync(s);
    }
}
