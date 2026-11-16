using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

/// <summary>One choice of what the Nitro key does.</summary>
public sealed partial class KeyActionOption(NitroKeyAction action, string iconKey, Func<KeyActionOption, Task> select) : ObservableObject
{
    public NitroKeyAction Action { get; } = action;
    public string Title => Loc.T($"key.{NitroKeyActions.ToText(Action)}");
    public string Description => Loc.T($"key.{NitroKeyActions.ToText(Action)}.desc");
    public Geometry? Icon => Visuals.Icon(iconKey);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private Task Select() => select(this);

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>What the Nitro key does, and the firmware mode key on models that have one.</summary>
public sealed partial class NitroKeyViewModel : PageViewModel
{
    public NitroKeyViewModel(MainViewModel main) : base(main)
    {
        Actions =
        [
            new(NitroKeyAction.Turbo, "ico.flame", Choose),
            new(NitroKeyAction.Cycle, "ico.cycle", Choose),
            new(NitroKeyAction.Gui, "ico.window", Choose),
            new(NitroKeyAction.None, "ico.off", Choose),
        ];
    }

    public override string Key => "nitrokey";
    public override string IconKey => "ico.nitrokey";

    public IReadOnlyList<KeyActionOption> Actions { get; }

    private Task Choose(KeyActionOption option) =>
        Session.Do(api => api.SetNitroKeyAction(NitroKeyActions.ToText(option.Action)));

    [RelayCommand]
    private async Task Test()
    {
        var change = await Session.Call(api => api.PressNitroKey() ?? new ModeChange(null, "", null, GpuApplyResult.NotApplicable) { Changed = false });
        if (change is { Changed: true } c)
            Main.Toast(c.Turbo is { } t
                ? Loc.T(t ? "turbo.on" : "turbo.off")
                : Loc.F("key.test.mode", Visuals.ModeName(c.To)));
    }

    public bool HasModeKey => Status?.DriverModeKey is not null;
    public bool ModeKeyCycles => Status?.DriverModeKey == ModeKeyBehavior.Cycle;
    public bool ModeKeyTurbo => Status?.DriverModeKey == ModeKeyBehavior.TurboToggle;

    [RelayCommand]
    private Task SetModeKey(string behavior) => Session.Do(api => api.SetModeKey(behavior));

    protected override void OnStatus(NitroStatus s)
    {
        foreach (var a in Actions)
            a.IsSelected = a.Action == s.NitroKeyAction;
        OnPropertyChanged(nameof(HasModeKey));
        OnPropertyChanged(nameof(ModeKeyCycles));
        OnPropertyChanged(nameof(ModeKeyTurbo));
    }

    public override void OnLanguageChanged()
    {
        foreach (var a in Actions)
            a.Relocalize();
        base.OnLanguageChanged();
    }
}
