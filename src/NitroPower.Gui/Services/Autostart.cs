namespace NitroPower.Gui.Services;

/// <summary>~/.config/autostart/nitropower.desktop: start in the tray at login.</summary>
public static class Autostart
{
    public static string FilePath => Path.Join(AppSettings.ConfigHome, "autostart", "nitropower.desktop");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                return File.Exists(FilePath) &&
                       !File.ReadAllLines(FilePath).Any(l => l.Trim() is "Hidden=true" or "X-GNOME-Autostart-enabled=false");
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """
            [Desktop Entry]
            Type=Application
            Name=NitroPower
            Comment=Acer Nitro/Predator control (tray)
            Exec=nitropower --hidden
            Icon=nitropower
            Terminal=false
            X-GNOME-Autostart-enabled=true
            X-GNOME-Autostart-Delay=3

            """);
    }
}
