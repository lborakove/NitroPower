using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NitroPower.Core.Api;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.ViewModels;

/// <summary>One thermal mode as a selectable card.</summary>
public sealed partial class ModeItem : ObservableObject
{
    private readonly Func<ModeItem, Task> _select;

    public ModeItem(ModeInfo info, Func<ModeItem, Task> select)
    {
        Info = info;
        _select = select;
    }

    public ModeInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Visuals.ModeName(Id);
    public string Description => Visuals.ModeDescription(Id);
    public Geometry? Icon => Visuals.Icon(Visuals.ModeIconKey(Id));
    public IBrush Brush => Visuals.ModeBrush(Id);
    public IBrush SoftBrush => Visuals.ModeSoftBrush(Id);

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial bool IsAvailable { get; set; } = true;

    /// <summary>GPU offsets this mode brings, e.g. "GPU +100/+200".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial string? GpuText { get; set; }

    public string Note => !IsAvailable ? Loc.T("mode.needsAc") : GpuText ?? (Info.RequiresAc ? Loc.T("mode.acOnly") : Loc.T("mode.anyPower"));

    [RelayCommand]
    private Task Select() => _select(this);
}
