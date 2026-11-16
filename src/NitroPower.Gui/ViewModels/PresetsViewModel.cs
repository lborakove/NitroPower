using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

public sealed partial class PresetItem(string name, Preset preset, PresetsViewModel owner) : ObservableObject
{
    public string Name { get; } = name;
    public Preset Preset { get; } = preset;

    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string>();
            if (Preset.Mode is { } m)
                lines.Add(Loc.F("preset.mode", Visuals.ModeName(m)) + (Preset.Turbo == true ? " · " + Loc.T("mode.turbo") : ""));
            else if (Preset.Turbo == true)
                lines.Add(Loc.T("mode.turbo"));
            if (Preset.Fans is { } f)
                lines.Add(Loc.F("preset.fans", Visuals.FanChoice(f)));
            if (Preset.GpuOverclock is { } oc)
                lines.Add(Loc.F("preset.gpu", Loc.T(oc ? "common.on" : "common.off")));
            if (Preset.BatteryLimiter is { } bl)
                lines.Add(Loc.F("preset.limiter", Loc.T(bl ? "common.on" : "common.off")));
            if (Preset.UsbCharging is { } usb)
                lines.Add(Loc.F("preset.usb", usb == 0 ? Loc.T("common.off") : $"{usb}%"));
            if (Preset.KeyboardTimeout is { } kt)
                lines.Add(Loc.F("preset.kbTimeout", Loc.T(kt ? "common.on" : "common.off")));
            if (Preset.LcdOverdrive is { } lcd)
                lines.Add(Loc.F("preset.lcd", Loc.T(lcd ? "common.on" : "common.off")));
            if (Preset.BootSound is { } bs)
                lines.Add(Loc.F("preset.bootSound", Loc.T(bs ? "common.on" : "common.off")));
            if (KeyboardEffectState.TryParse(Preset.KeyboardEffect) is { } e)
                lines.Add(Loc.F("preset.rgb", Loc.T($"rgb.effect.{e.Effect.ToString().ToLowerInvariant()}")));
            else if (Preset.KeyboardZones is not null)
                lines.Add(Loc.F("preset.rgb", Loc.T("rgb.zones")));
            return lines;
        }
    }

    public Avalonia.Media.IBrush Brush => Visuals.ModeBrush(Preset.Turbo == true ? "turbo" : Preset.Mode);

    [RelayCommand]
    private Task Apply() => owner.Apply(this);

    [RelayCommand]
    private Task Delete() => owner.Delete(this);
}

/// <summary>Named sets of settings, kept by the daemon in /etc/nitropower/presets.</summary>
public sealed partial class PresetsViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Key => "presets";
    public override string IconKey => "ico.layers";

    public ObservableCollection<PresetItem> Presets { get; } = [];

    public bool IsEmpty => Presets.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string NewName { get; set; } = "";

    public string? NameError => NewName.Length == 0 || PresetStore.IsValidName(NewName) ? null : Loc.T("err.presetName");

    private bool CanSave => NewName.Length > 0 && PresetStore.IsValidName(NewName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var name = NewName.Trim();
        if (Presets.Any(p => p.Name == name) &&
            !await Main.AskAsync(Loc.T("preset.overwrite.title"), Loc.F("preset.overwrite.text", name), Loc.T("preset.overwrite")))
            return;
        if (await Session.Do(api => api.SavePreset(name)))
        {
            Main.Toast(Loc.F("preset.saved", name));
            NewName = "";
            await Reload();
        }
    }

    public async Task Apply(PresetItem item)
    {
        var results = await Session.Call(api => api.LoadPreset(item.Name));
        if (results is null)
            return;
        var failed = results.Where(r => !r.Ok).ToList();
        Main.Toast(failed.Count == 0
                ? Loc.F("preset.applied", item.Name)
                : Loc.F("preset.partly", item.Name, string.Join("; ", failed.Select(f => $"{f.Setting}: {Services.Messages.Translate(f.Error!)}"))),
            failed.Count > 0);
    }

    public async Task Delete(PresetItem item)
    {
        if (!await Main.AskAsync(Loc.T("preset.delete.title"), Loc.F("preset.delete.text", item.Name), Loc.T("common.delete"), danger: true))
            return;
        if (await Session.Do(api => api.DeletePreset(item.Name)))
            await Reload();
    }

    [RelayCommand]
    public async Task Reload()
    {
        var all = await Session.Query(api => api.ListPresets()
            .Select(name => (name, preset: api.GetPreset(name)))
            .Where(x => x.preset is not null)
            .Select(x => (x.name, preset: x.preset!))
            .ToList());
        if (all is null)
            return;
        Presets.Clear();
        foreach (var (name, preset) in all)
            Presets.Add(new PresetItem(name, preset, this));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public override void OnShown() => _ = Reload();

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        _ = Reload();
    }
}
