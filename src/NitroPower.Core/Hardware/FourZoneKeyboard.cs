using System.Globalization;
using NitroPower.Core.IO;

namespace NitroPower.Core.Hardware;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black => default;

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"{R:x2}{G:x2}{B:x2}");

    public override string ToString() => ToHex();

    /// <summary>"rrggbb" or "#rrggbb".</summary>
    public static Rgb? TryParseHex(string? text)
    {
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } ||
            !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return null;
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>Effects of the firmware keyboard engine (WMI SetGamingKBBacklight).
/// The numeric values are the firmware's.</summary>
public enum KeyboardEffect
{
    Static = 0,
    Breathing = 1,
    Neon = 2,
    Wave = 3,
    Shifting = 4,
    Zoom = 5,
    Meteor = 6,
    Twinkling = 7,
}

public enum KeyboardDirection
{
    None = 0,
    RightToLeft = 1,
    LeftToRight = 2,
}

/// <summary>One firmware lighting effect for the whole keyboard
/// (four_zoned_kb/four_zone_mode).</summary>
public sealed record KeyboardEffectState(
    KeyboardEffect Effect,
    int Speed,
    int Brightness,
    KeyboardDirection Direction,
    Rgb Color)
{
    public const int MaxSpeed = 9;
    public const int MaxBrightness = 100;

    /// <summary>Neon and wave draw their own colors.</summary>
    public static bool UsesColor(KeyboardEffect e) => e is not (KeyboardEffect.Neon or KeyboardEffect.Wave);

    /// <summary>Only wave and shifting move in a direction, and the firmware requires one.</summary>
    public static bool UsesDirection(KeyboardEffect e) => e is KeyboardEffect.Wave or KeyboardEffect.Shifting;

    public static bool UsesSpeed(KeyboardEffect e) => e is not KeyboardEffect.Static;

    /// <summary>Same fields the driver zeroes before calling the firmware, so a
    /// normalized state compares equal to what is read back.</summary>
    public KeyboardEffectState Normalize() => this with
    {
        Speed = UsesSpeed(Effect) ? Speed : 0,
        Direction = UsesDirection(Effect) ? Direction : KeyboardDirection.None,
        Color = UsesColor(Effect) ? Color : Rgb.Black,
    };

    /// <summary>Rejects what the driver would answer with -EINVAL.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Effect))
            throw new ArgumentOutOfRangeException(nameof(Effect), Effect, "unknown effect");
        if (Speed is < 0 or > MaxSpeed)
            throw new ArgumentOutOfRangeException(nameof(Speed), Speed, $"speed must be 0..{MaxSpeed}");
        if (Brightness is < 0 or > MaxBrightness)
            throw new ArgumentOutOfRangeException(nameof(Brightness), Brightness, $"brightness must be 0..{MaxBrightness}");
        if (!Enum.IsDefined(Direction))
            throw new ArgumentOutOfRangeException(nameof(Direction), Direction, "unknown direction");
        if (UsesDirection(Effect) && Direction == KeyboardDirection.None)
            throw new ArgumentException($"{Effect} needs a direction", nameof(Direction));
    }

    /// <summary>"mode,speed,brightness,direction,red,green,blue".</summary>
    public string ToSysfs() => string.Create(CultureInfo.InvariantCulture,
        $"{(int)Effect},{Speed},{Brightness},{(int)Direction},{Color.R},{Color.G},{Color.B}");

    /// <summary>Parses the driver's output; null when it is malformed or out of
    /// range (firmware without the keyboard engine answers with junk).</summary>
    public static KeyboardEffectState? TryParse(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 7 })
            return null;
        var v = new int[7];
        for (var i = 0; i < 7; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                return null;
        }
        if (v[0] is < 0 or > 7 || v[1] is < 0 or > MaxSpeed || v[2] is < 0 or > MaxBrightness ||
            v[3] is < 0 or > 2 || v[4] is < 0 or > 255 || v[5] is < 0 or > 255 || v[6] is < 0 or > 255)
            return null;
        return new KeyboardEffectState((KeyboardEffect)v[0], v[1], v[2], (KeyboardDirection)v[3],
            new Rgb((byte)v[4], (byte)v[5], (byte)v[6]));
    }
}

/// <summary>A static color per keyboard zone, left to right
/// (four_zoned_kb/per_zone_mode).</summary>
public sealed record PerZoneColors(Rgb Zone1, Rgb Zone2, Rgb Zone3, Rgb Zone4, int Brightness)
{
    public const int ZoneCount = 4;

    public IReadOnlyList<Rgb> Zones => [Zone1, Zone2, Zone3, Zone4];

    public static PerZoneColors Uniform(Rgb color, int brightness) =>
        new(color, color, color, color, brightness);

    public static PerZoneColors FromZones(IReadOnlyList<Rgb> zones, int brightness)
    {
        if (zones.Count != ZoneCount)
            throw new ArgumentException($"need {ZoneCount} zones, got {zones.Count}", nameof(zones));
        return new PerZoneColors(zones[0], zones[1], zones[2], zones[3], brightness);
    }

    public void Validate()
    {
        if (Brightness is < 0 or > KeyboardEffectState.MaxBrightness)
            throw new ArgumentOutOfRangeException(nameof(Brightness), Brightness,
                $"brightness must be 0..{KeyboardEffectState.MaxBrightness}");
    }

    /// <summary>"rrggbb,rrggbb,rrggbb,rrggbb,brightness".</summary>
    public string ToSysfs() => string.Create(CultureInfo.InvariantCulture,
        $"{Zone1.ToHex()},{Zone2.ToHex()},{Zone3.ToHex()},{Zone4.ToHex()},{Brightness}");

    public static PerZoneColors? TryParse(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 5 } ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var brightness) ||
            brightness is < 0 or > KeyboardEffectState.MaxBrightness)
            return null;
        var zones = new Rgb[ZoneCount];
        for (var i = 0; i < ZoneCount; i++)
        {
            if (Rgb.TryParseHex(parts[i]) is not { } c)
                return null;
            zones[i] = c;
        }
        return FromZones(zones, brightness);
    }
}

/// <summary>
/// The four-zone RGB keyboard of linuwu_sense (four_zoned_kb). The driver
/// keeps the last state in /etc/four_zone_kb_state and restores it at boot.
/// </summary>
public sealed class FourZoneKeyboard
{
    public const string GroupPath = NitroSenseDevice.PlatformPath + "/four_zoned_kb";
    public const string EffectPath = GroupPath + "/four_zone_mode";
    public const string ZonesPath = GroupPath + "/per_zone_mode";

    private readonly ISysfs _fs;

    private FourZoneKeyboard(ISysfs fs) => _fs = fs;

    public static FourZoneKeyboard? Detect(ISysfs fs) =>
        fs.Exists(EffectPath) ? new FourZoneKeyboard(fs) : null;

    public KeyboardEffectState? GetEffect() => KeyboardEffectState.TryParse(_fs.ReadText(EffectPath));

    public void SetEffect(KeyboardEffectState state)
    {
        state.Validate();
        _fs.WriteText(EffectPath, state.Normalize().ToSysfs());
    }

    public PerZoneColors? GetZones() => PerZoneColors.TryParse(_fs.ReadText(ZonesPath));

    public void SetZones(PerZoneColors colors)
    {
        colors.Validate();
        _fs.WriteText(ZonesPath, colors.ToSysfs());
    }
}
