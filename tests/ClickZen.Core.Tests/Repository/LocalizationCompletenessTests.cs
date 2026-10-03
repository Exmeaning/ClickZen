using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClickZen.Core.Tests.Repository;

/// <summary>
/// Guards the two UI languages: both resw files must define exactly the same keys, and every
/// x:Uid used in XAML must have at least one property entry ("Uid.Property") in the resources.
/// </summary>
public sealed partial class LocalizationCompletenessTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (ClickZen.slnx) not found.");
    }

    private static string StringsDir => Path.Combine(RepoRoot(), "src", "ClickZen.App", "Strings");

    private static HashSet<string> Keys(string lang) =>
        XDocument.Load(Path.Combine(StringsDir, lang, "Resources.resw"))
            .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Chinese_and_English_define_the_same_keys()
    {
        var zh = Keys("zh-CN");
        var en = Keys("en-US");

        Assert.Empty(zh.Except(en));
        Assert.Empty(en.Except(zh));
        Assert.NotEmpty(zh);
    }

    [Fact]
    public void Every_xaml_uid_has_resources()
    {
        var keys = Keys("zh-CN");
        var appDir = Path.Combine(RepoRoot(), "src", "ClickZen.App");
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match m in UidRegex().Matches(File.ReadAllText(file)))
            {
                var uid = m.Groups[1].Value;
                if (!keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)))
                {
                    missing.Add($"{Path.GetFileName(file)}: {uid}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Resources_contain_no_emoji()
    {
        foreach (var lang in new[] { "zh-CN", "en-US" })
        {
            var text = File.ReadAllText(Path.Combine(StringsDir, lang, "Resources.resw"));
            var emoji = text.EnumerateRunes().Where(r => r.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF).ToList();
            Assert.Empty(emoji);
        }
    }

    [GeneratedRegex("x:Uid=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex UidRegex();
}
