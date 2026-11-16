using System.Text.RegularExpressions;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Gui.Localization;

namespace NitroPower.Gui.Tests;

public partial class LocalizationTests
{
    private static string GuiSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "NitroPower.sln")))
            dir = dir.Parent;
        return Path.Join(dir?.FullName ?? throw new InvalidOperationException("repository not found"), "src", "NitroPower.Gui");
    }

    [GeneratedRegex(@"\{l:Tr ([A-Za-z0-9_.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"Loc\.[TF]\(""([A-Za-z0-9_.]+)""")]
    private static partial Regex CodeKey();

    /// <summary>Loc.T(x ? "a.b" : "c.d"), Loc.F(x ? "a.b" : "c.d", ...).</summary>
    [GeneratedRegex(@"Loc\.[TF]\([^;()]*?\?\s*""([A-Za-z0-9_.]+)""\s*:\s*""([A-Za-z0-9_.]+)""")]
    private static partial Regex ConditionalKey();

    [Fact]
    public void Every_key_in_the_views_and_code_has_both_languages()
    {
        var root = GuiSource();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in XamlKey().Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);
        }
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !f.EndsWith("Strings.cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKey().Matches(text))
                used.Add(m.Groups[1].Value);
            foreach (Match m in ConditionalKey().Matches(text))
                used.UnionWith([m.Groups[1].Value, m.Groups[2].Value]);
        }

        // Keys built at run time
        foreach (var page in new[] { "dashboard", "performance", "fans", "battery", "device", "nitrokey", "presets", "system" })
            used.UnionWith([$"nav.{page}", $"nav.{page}.sub"]);
        foreach (var mode in new[] { "eco", "quiet", "balanced", "performance" })
            used.UnionWith([$"mode.{mode}", $"mode.{mode}.desc"]);
        foreach (var feature in NitroFeatures.Attributes.Keys)
            used.UnionWith([$"feature.{feature}", $"feature.{feature}.desc"]);
        foreach (var action in Enum.GetValues<NitroKeyAction>().Select(NitroKeyActions.ToText))
            used.UnionWith([$"key.{action}", $"key.{action}.desc"]);
        foreach (var effect in Enum.GetValues<KeyboardEffect>())
            used.Add($"rgb.effect.{effect.ToString().ToLowerInvariant()}");

        var missing = used.Where(k => !Strings.All.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "missing texts: " + string.Join(", ", missing));
        Assert.True(used.Count > 250, $"only {used.Count} keys found: the scan is broken");
    }

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void Both_languages_have_text_and_the_same_placeholders()
    {
        foreach (var (key, (pt, en)) in Strings.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(pt), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            var a = Placeholder().Matches(pt).Select(m => m.Groups[1].Value).Order().ToList();
            var b = Placeholder().Matches(en).Select(m => m.Groups[1].Value).Order().ToList();
            Assert.True(a.SequenceEqual(b), $"{key}: placeholders differ");
        }
    }

    [Fact]
    public void Language_follows_the_environment_and_can_be_forced()
    {
        Assert.Equal("Painel", Strings.Get("nav.dashboard", Loc.Portuguese));
        Assert.Equal("Dashboard", Strings.Get("nav.dashboard", Loc.English));
        Assert.Equal("no.such.key", Strings.Get("no.such.key", Loc.English));
    }
}
