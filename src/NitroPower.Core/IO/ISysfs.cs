namespace NitroPower.Core.IO;

/// <summary>
/// Access to kernel pseudo-files (sysfs, procfs). Paths are absolute, such as
/// "/sys/firmware/acpi/platform_profile". Tests swap in a fake tree.
/// </summary>
public interface ISysfs
{
    bool Exists(string path);

    /// <summary>File contents without surrounding whitespace, or null when the
    /// file is missing or the kernel refuses the read (EIO, EACCES, ...).</summary>
    string? ReadText(string path);

    /// <summary>Writes a value. The kernel's errors surface as IOException or
    /// UnauthorizedAccessException.</summary>
    void WriteText(string path, string value);

    /// <summary>Absolute paths of the subdirectories of <paramref name="path"/>,
    /// following symlinks (as in /sys/class/*). Empty when the directory is missing.</summary>
    IReadOnlyList<string> ListDirectories(string path);

    /// <summary>Absolute paths of the regular files in <paramref name="path"/>
    /// (as in /sys/module/*/parameters). Empty when the directory is missing.</summary>
    IReadOnlyList<string> ListFiles(string path);
}
