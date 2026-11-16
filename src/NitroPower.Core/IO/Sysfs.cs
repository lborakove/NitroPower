namespace NitroPower.Core.IO;

/// <summary>
/// <see cref="ISysfs"/> over the real filesystem. An optional root prefix maps
/// "/sys/..." to "&lt;root&gt;/sys/..." so tests can use a temporary tree.
/// </summary>
public sealed class Sysfs : ISysfs
{
    private readonly string _root;

    public Sysfs(string root = "/")
    {
        _root = Path.GetFullPath(root);
    }

    public static Sysfs Real { get; } = new();

    private string Map(string path)
    {
        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"Path must be absolute: {path}", nameof(path));
        return _root == "/" ? path : Path.Join(_root, path.TrimStart('/'));
    }

    private string Unmap(string physical)
    {
        if (_root == "/")
            return physical;
        var relative = Path.GetRelativePath(_root, physical);
        return "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public bool Exists(string path)
    {
        var p = Map(path);
        return File.Exists(p) || Directory.Exists(p);
    }

    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(Map(path)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteText(string path, string value)
    {
        // sysfs attributes take a single write; WriteAllText truncates and writes once.
        File.WriteAllText(Map(path), value);
    }

    public IReadOnlyList<string> ListDirectories(string path)
    {
        var p = Map(path);
        if (!Directory.Exists(p))
            return [];
        try
        {
            return Directory.GetFileSystemEntries(p)
                .Where(Directory.Exists) // keeps symlinks to directories
                .Select(Unmap)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> ListFiles(string path)
    {
        var p = Map(path);
        if (!Directory.Exists(p))
            return [];
        try
        {
            return Directory.GetFiles(p).Select(Unmap).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
