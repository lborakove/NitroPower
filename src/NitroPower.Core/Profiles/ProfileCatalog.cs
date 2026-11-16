using System.Text.Json;

namespace NitroPower.Core.Profiles;

/// <summary>
/// The model profiles found on disk. Directories are read in order, and a later
/// profile with the same id replaces an earlier one, so /etc can override /usr/share.
/// </summary>
public sealed class ProfileCatalog
{
    public static readonly string[] DefaultDirectories =
        ["/usr/share/nitropower/profiles", "/etc/nitropower/profiles"];

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<ModelProfile> Profiles { get; }

    /// <summary>Files that could not be used, with the reason. Not fatal.</summary>
    public IReadOnlyList<string> Errors { get; }

    private ProfileCatalog(IReadOnlyList<ModelProfile> profiles, IReadOnlyList<string> errors)
    {
        Profiles = profiles;
        Errors = errors;
    }

    public static ProfileCatalog Load(IEnumerable<string> directories)
    {
        var byId = new Dictionary<string, ModelProfile>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file).EndsWith(".schema.json", StringComparison.Ordinal))
                    continue;
                try
                {
                    var profile = Parse(File.ReadAllText(file));
                    byId[profile.Id] = profile;
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
                {
                    errors.Add($"{file}: {e.Message}");
                }
            }
        }
        return new ProfileCatalog(byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(), errors);
    }

    public static ModelProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<ModelProfile>(json, JsonOptions)
                      ?? throw new InvalidDataException("empty profile");
        Validate(profile);
        return profile;
    }

    private static void Validate(ModelProfile p)
    {
        if (p.SchemaVersion != ModelProfile.SupportedSchemaVersion)
            throw new InvalidDataException(
                $"schemaVersion {p.SchemaVersion} is not supported (expected {ModelProfile.SupportedSchemaVersion})");
        if (string.IsNullOrWhiteSpace(p.Id))
            throw new InvalidDataException("missing id");
        if (p.Match?.Dmi is null || p.Match.Dmi.Count == 0)
            throw new InvalidDataException("match.dmi must name at least one field");
        if (p.ThermalModes?.Modes is null || p.ThermalModes.Modes.Count == 0)
            throw new InvalidDataException("thermalModes.modes is empty");
        if (p.ThermalModes.Modes.Select(m => m.Id).Distinct().Count() != p.ThermalModes.Modes.Count)
            throw new InvalidDataException("duplicate thermal mode id");
        foreach (var id in (p.ModeCycle?.Ac ?? []).Concat(p.ModeCycle?.Battery ?? []).Concat(p.ModeCycle?.FansAutoOn ?? []))
        {
            if (p.FindMode(id) is null)
                throw new InvalidDataException($"modeCycle names unknown mode '{id}'");
        }
        if (p.ModeCycle is { } cycle && (cycle.Ac.Count == 0 || cycle.Battery.Count == 0))
            throw new InvalidDataException("modeCycle.ac and modeCycle.battery need at least one mode");
        if (p.Turbo is { } turbo)
        {
            if (p.FindMode(turbo.Mode) is null)
                throw new InvalidDataException($"turbo.mode names unknown mode '{turbo.Mode}'");
            if (FanSetting.TryParse(turbo.Fans) is null)
                throw new InvalidDataException($"turbo.fans '{turbo.Fans}' is not max, auto, keep or cpu,gpu");
        }
        foreach (var device in p.GpuOverclock?.Devices ?? [])
        {
            foreach (var level in device.Levels)
            {
                if (level.Core is < -1000 or > 1000 || level.Mem is < -1000 or > 3000)
                    throw new InvalidDataException($"GPU offset out of range for {device.PciId}");
            }
        }
    }

    /// <summary>The profile whose DMI fields all equal this machine's.</summary>
    public ModelProfile? Match(DmiInfo dmi) =>
        Profiles.FirstOrDefault(p => p.Match.Dmi.All(kv =>
            string.Equals(dmi[kv.Key], kv.Value, StringComparison.Ordinal)));
}
