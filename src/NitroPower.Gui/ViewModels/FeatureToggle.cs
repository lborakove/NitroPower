using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

/// <summary>An on/off firmware feature (battery limiter, keyboard timeout...).</summary>
public sealed partial class FeatureToggle(string id, string iconKey, Func<FeatureToggle, bool, Task<bool>> change) : ObservableObject
{
    private bool _syncing;

    public string Id { get; } = id;
    public string Title => Loc.T($"feature.{Id}");
    public string Description => Loc.T($"feature.{Id}.desc");
    public Geometry? Icon => Visuals.Icon(iconKey);

    /// <summary>The model has it (the driver offers the attribute).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note), nameof(CanChange))]
    public partial bool IsPresent { get; private set; }

    /// <summary>Present, but the firmware did not answer the read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note), nameof(CanChange))]
    public partial bool IsUnknown { get; private set; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>Not when the state is unknown: the switch could not show what a change did.</summary>
    public bool CanChange => IsPresent && !IsUnknown;

    public string? Note => !IsPresent ? Loc.T("feature.absent") : IsUnknown ? Loc.T("feature.unknown") : null;

    public void Load(IReadOnlyDictionary<string, bool?> features)
    {
        _syncing = true;
        try
        {
            IsPresent = features.TryGetValue(Id, out var value);
            IsUnknown = IsPresent && value is null;
            IsOn = value == true;
        }
        finally
        {
            _syncing = false;
        }
    }

    async partial void OnIsOnChanged(bool value)
    {
        if (_syncing)
            return;
        if (!await change(this, value))
        {
            _syncing = true;
            IsOn = !value;
            _syncing = false;
        }
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Note));
    }
}
