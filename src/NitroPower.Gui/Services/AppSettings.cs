using System.Text.Json;
using System.Text.Json.Serialization;

namespace NitroPower.Gui.Services;

/// <summary>Preferences of this user's NitroPower window (not the hardware:
/// that lives in the daemon). Stored in ~/.config/nitropower/gui.json.</summary>
public sealed record AppSettings
{
    /// <summary>"auto", "pt" or "en".</summary>
    public string Language { get; init; } = "auto";

    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; init; } = "dark";

    /// <summary>Closing the window keeps NitroPower in the tray.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Sensor refresh while the window is open.</summary>
    public int RefreshMs { get; init; } = 1000;

    public static readonly int[] RefreshChoices = [1000, 2000, 5000];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath => Path.Join(ConfigHome, "nitropower", "gui.json");

    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    public static AppSettings Load(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            return s with { RefreshMs = RefreshChoices.Contains(s.RefreshMs) ? s.RefreshMs : 1000 };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json) + "\n");
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; the window keeps working without them
        }
    }
}
