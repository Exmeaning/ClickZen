using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClickZen.Core.Tests.Localization;

/// <summary>
/// Resource integrity: both languages define the same keys, every x:Uid in XAML has at least one
/// property entry, and every key looked up from code (loc["Key"] / loc.Format("Key", …)) exists.
/// Reads the source tree, so it needs no WinUI at test time.
/// </summary>
public sealed partial class ResourceIntegrityTests
{
    private static readonly string AppDir = FindAppDir();

    private static string FindAppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        return dir is null ? "" : Path.Combine(dir.FullName, "src", "ClickZen.App");
    }

    private static Dictionary<string, string> LoadResw(string lang)
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "Strings", lang, "Resources.resw"));
        return doc.Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "", StringComparer.Ordinal);
    }

    [Fact]
    public void Languages_define_the_same_non_empty_keys()
    {
        Assert.SkipWhen(!Directory.Exists(AppDir), "Source tree not found.");
        var zh = LoadResw("zh-CN");
        var en = LoadResw("en-US");

        Assert.Empty(zh.Keys.Except(en.Keys, StringComparer.Ordinal));
        Assert.Empty(en.Keys.Except(zh.Keys, StringComparer.Ordinal));
        Assert.Empty(zh.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => "zh-CN:" + kv.Key)
            .Concat(en.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => "en-US:" + kv.Key)));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        Assert.SkipWhen(!Directory.Exists(AppDir), "Source tree not found.");
        var zh = LoadResw("zh-CN");
        var en = LoadResw("en-US");
        var mismatched = zh.Keys.Where(en.ContainsKey)
            .Where(k => !Placeholders(zh[k]).SetEquals(Placeholders(en[k])))
            .ToList();
        Assert.Empty(mismatched);
    }

    [Fact]
    public void Every_xuid_has_resources()
    {
        Assert.SkipWhen(!Directory.Exists(AppDir), "Source tree not found.");
        var keys = LoadResw("en-US").Keys.Select(k => k.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var missing = Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => UidRegex().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Uid: m.Groups[1].Value)))
            .Where(u => !keys.Contains(u.Uid))
            .Select(u => $"{u.File}: {u.Uid}")
            .Distinct()
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_key_used_from_code_exists()
    {
        Assert.SkipWhen(!Directory.Exists(AppDir), "Source tree not found.");
        var keys = LoadResw("en-US").Keys.ToHashSet(StringComparer.Ordinal);
        var missing = Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f))
            .SelectMany(f => CodeKeyRegex().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups["key"].Value)))
            .Where(u => u.Key.EndsWith('_')
                ? !keys.Any(k => k.StartsWith(u.Key, StringComparison.Ordinal)) // prefix of a composed key: loc["X_" + value]
                : !keys.Contains(u.Key))
            .Select(u => $"{u.File}: {u.Key}")
            .Distinct()
            .ToList();
        Assert.Empty(missing);
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static HashSet<string> Placeholders(string s) => PlaceholderRegex().Matches(s).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex("x:Uid=\"([^\"]+)\"")]
    private static partial Regex UidRegex();

    // _loc["Key"], Loc["Key"], loc["Key"], _loc.Format("Key", ...), Loc.Format("Key" ...
    [GeneratedRegex(@"\b_?[Ll]oc(?:\.Format\(|\[)""(?<key>[A-Za-z][A-Za-z0-9_]*)""")]
    private static partial Regex CodeKeyRegex();

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex PlaceholderRegex();
}
