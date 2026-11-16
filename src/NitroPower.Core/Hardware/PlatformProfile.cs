using NitroPower.Core.IO;

namespace NitroPower.Core.Hardware;

/// <summary>The kernel's ACPI platform profile, which linuwu_sense maps to the
/// firmware operating mode. Writing needs root.</summary>
public sealed class PlatformProfile(ISysfs fs)
{
    public const string CurrentPath = "/sys/firmware/acpi/platform_profile";
    public const string ChoicesPath = "/sys/firmware/acpi/platform_profile_choices";

    public bool IsAvailable => fs.Exists(CurrentPath);

    public string? Current => fs.ReadText(CurrentPath);

    public IReadOnlyList<string> Choices =>
        fs.ReadText(ChoicesPath)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public void Set(string profile)
    {
        if (!Choices.Contains(profile, StringComparer.Ordinal))
            throw new ArgumentException($"'{profile}' is not one of: {string.Join(", ", Choices)}", nameof(profile));
        fs.WriteText(CurrentPath, profile);
    }
}
