using NitroPower.Core.IO;

namespace NitroPower.Core.Tests;

/// <summary>A throwaway directory tree standing in for / (sysfs, procfs).</summary>
public sealed class TestTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("nitropower-test-").FullName;

    public Sysfs Fs => new(Root);

    public TestTree File(string path, string content)
    {
        var physical = Path.Join(Root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        System.IO.File.WriteAllText(physical, content + "\n");
        return this;
    }

    public TestTree Dir(string path)
    {
        Directory.CreateDirectory(Path.Join(Root, path.TrimStart('/')));
        return this;
    }

    public string Read(string path) =>
        System.IO.File.ReadAllText(Path.Join(Root, path.TrimStart('/')));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The repository's profiles/ directory, found from the test binary.</summary>
    public static string RepoProfilesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Join(dir.FullName, "NitroPower.sln")))
            dir = dir.Parent;
        return Path.Join(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "profiles");
    }
}
