using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

public sealed class EffectOption(KeyboardEffect effect)
{
    public KeyboardEffect Effect { get; } = effect;
    public string Name => Loc.T($"rgb.effect.{Effect.ToString().ToLowerInvariant()}");
    public override string ToString() => Name;
}

/// <summary>Keyboard light, display and boot options, and the 4-zone RGB keyboard.</summary>
public sealed partial class DeviceViewModel : PageViewModel
{
    public DeviceViewModel(MainViewModel main) : base(main)
    {
        KeyboardTimeout = new FeatureToggle(NitroFeatures.KeyboardTimeout, "ico.bulb", Change);
        LcdOverdrive = new FeatureToggle(NitroFeatures.LcdOverdrive, "ico.display", Change);
        BootSound = new FeatureToggle(NitroFeatures.BootSound, "ico.sound", Change);
        Effects = Enum.GetValues<KeyboardEffect>().Select(e => new EffectOption(e)).ToList();
        SelectedEffect = Effects[0];
    }

    public override string Key => "device";
    public override string IconKey => "ico.keyboard";

    public FeatureToggle KeyboardTimeout { get; }
    public FeatureToggle LcdOverdrive { get; }
    public FeatureToggle BootSound { get; }
    public IReadOnlyList<FeatureToggle> Toggles => [KeyboardTimeout, LcdOverdrive, BootSound];

    private async Task<bool> Change(FeatureToggle f, bool on) => await Session.Do(api => api.SetFeature(f.Id, on));

    public bool HasRgb => Status?.Keyboard is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingEffect))]
    public partial bool EditingZones { get; set; }

    public bool EditingEffect => !EditingZones;

    [RelayCommand]
    private void EditTab(string tab) => EditingZones = tab == "zones";

    public IReadOnlyList<EffectOption> Effects { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesColor), nameof(UsesSpeed), nameof(UsesDirection), nameof(PreviewZone1),
        nameof(PreviewZone2), nameof(PreviewZone3), nameof(PreviewZone4))]
    public partial EffectOption SelectedEffect { get; set; }

    public bool UsesColor => KeyboardEffectState.UsesColor(SelectedEffect.Effect);
    public bool UsesSpeed => KeyboardEffectState.UsesSpeed(SelectedEffect.Effect);
    public bool UsesDirection => KeyboardEffectState.UsesDirection(SelectedEffect.Effect);

    [ObservableProperty]
    public partial int Speed { get; set; } = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBrightness))]
    public partial int Brightness { get; set; } = 100;

    [ObservableProperty]
    public partial bool RightToLeft { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewZone1), nameof(PreviewZone2), nameof(PreviewZone3), nameof(PreviewZone4))]
    public partial Color EffectColor { get; set; } = Colors.White;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewZone1))]
    public partial Color Zone1 { get; set; } = Colors.White;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewZone2))]
    public partial Color Zone2 { get; set; } = Colors.White;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewZone3))]
    public partial Color Zone3 { get; set; } = Colors.White;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewZone4))]
    public partial Color Zone4 { get; set; } = Colors.White;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBrightness))]
    public partial int ZoneBrightness { get; set; } = 100;

    partial void OnEditingZonesChanged(bool value)
    {
        foreach (var name in new[] { nameof(PreviewZone1), nameof(PreviewZone2), nameof(PreviewZone3), nameof(PreviewZone4), nameof(PreviewBrightness) })
            OnPropertyChanged(name);
    }

    private static readonly Color[] Rainbow = [Color.Parse("#F43F5E"), Color.Parse("#F59E0B"), Color.Parse("#34D399"), Color.Parse("#60A5FA")];

    private Color PreviewOf(int zone, Color zoneColor) =>
        EditingZones ? zoneColor : UsesColor ? EffectColor : Rainbow[zone];

    public Color PreviewZone1 => PreviewOf(0, Zone1);
    public Color PreviewZone2 => PreviewOf(1, Zone2);
    public Color PreviewZone3 => PreviewOf(2, Zone3);
    public Color PreviewZone4 => PreviewOf(3, Zone4);
    public double PreviewBrightness => EditingZones ? ZoneBrightness : Brightness;

    [RelayCommand]
    private void SameColor() => Zone2 = Zone3 = Zone4 = Zone1;

    private static Rgb ToRgb(Color c) => new(c.R, c.G, c.B);

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    [RelayCommand]
    private async Task ApplyEffect()
    {
        var effect = new KeyboardEffectState(SelectedEffect.Effect, UsesSpeed ? Math.Clamp(Speed, 1, 9) : 0, Brightness,
            UsesDirection ? (RightToLeft ? KeyboardDirection.RightToLeft : KeyboardDirection.LeftToRight) : KeyboardDirection.None,
            UsesColor ? ToRgb(EffectColor) : Rgb.Black);
        if (await Session.Do(api => api.SetKeyboardEffect(effect)))
            Main.Toast(Loc.T("rgb.applied"));
    }

    [RelayCommand]
    private async Task ApplyZones()
    {
        var zones = new PerZoneColors(ToRgb(Zone1), ToRgb(Zone2), ToRgb(Zone3), ToRgb(Zone4), ZoneBrightness);
        if (await Session.Do(api => api.SetKeyboardZones(zones)))
            Main.Toast(Loc.T("rgb.applied"));
    }

    private KeyboardStatus? _loaded;

    protected override void OnStatus(NitroStatus s)
    {
        foreach (var t in Toggles)
            t.Load(s.Features);
        if (s.Keyboard is { } kb && kb != _loaded)
        {
            _loaded = kb;
            if (kb.Effect is { } e)
            {
                SelectedEffect = Effects.First(x => x.Effect == e.Effect);
                Speed = e.Speed is >= 1 and <= 9 ? e.Speed : 5;
                Brightness = e.Brightness;
                RightToLeft = e.Direction == KeyboardDirection.RightToLeft;
                if (KeyboardEffectState.UsesColor(e.Effect))
                    EffectColor = ToColor(e.Color);
            }
            if (kb.Zones is { } z)
            {
                Zone1 = ToColor(z.Zone1);
                Zone2 = ToColor(z.Zone2);
                Zone3 = ToColor(z.Zone3);
                Zone4 = ToColor(z.Zone4);
                ZoneBrightness = z.Brightness;
            }
        }
        OnPropertyChanged(nameof(HasRgb));
    }

    public override void OnLanguageChanged()
    {
        foreach (var t in Toggles)
            t.Relocalize();
        var selected = SelectedEffect.Effect;
        Effects = Enum.GetValues<KeyboardEffect>().Select(e => new EffectOption(e)).ToList();
        SelectedEffect = Effects.First(e => e.Effect == selected);
        base.OnLanguageChanged();
    }
}
