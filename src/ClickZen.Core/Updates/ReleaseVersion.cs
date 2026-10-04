using System.Globalization;
using System.Text.Json;

namespace ClickZen.Core.Updates;

/// <summary>
/// A semantic-ish version as used by ClickZen tags ("v2.0.1", "2.1.0-beta.2", "2.0.0-dev").
/// Ordering follows SemVer 2.0: numeric core first, then a pre-release ranks below the same core
/// without one, and pre-release identifiers compare numerically or ordinally. Build metadata ("+abc") is ignored.
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string PreRelease) : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>Parses "v1.2.3", "1.2", "1.2.3-rc.1+meta"; missing minor/patch count as 0. Returns null for anything else.</summary>
    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        var pre = "";
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0)
            {
                return null;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }

        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            if (i < 3)
            {
                nums[i] = n;
            }
        }

        return new ReleaseVersion(nums[0], nums[1], nums[2], pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        if (c != 0)
        {
            return c;
        }

        if (!IsPreRelease || !other.IsPreRelease)
        {
            // A release ranks above any pre-release of the same core.
            return IsPreRelease == other.IsPreRelease ? 0 : IsPreRelease ? -1 : 1;
        }

        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = (an, bn) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1, // numeric identifiers rank lower than alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(ReleaseVersion? l, ReleaseVersion? r) => Compare(l, r) < 0;

    public static bool operator >(ReleaseVersion? l, ReleaseVersion? r) => Compare(l, r) > 0;

    public static bool operator <=(ReleaseVersion? l, ReleaseVersion? r) => Compare(l, r) <= 0;

    public static bool operator >=(ReleaseVersion? l, ReleaseVersion? r) => Compare(l, r) >= 0;

    private static int Compare(ReleaseVersion? l, ReleaseVersion? r) => l is null ? (r is null ? 0 : -1) : l.CompareTo(r);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (IsPreRelease ? "-" + PreRelease : "");
}

/// <summary>The fields ClickZen needs from GitHub's "latest release" API response.</summary>
public sealed record LatestRelease(string Tag, ReleaseVersion Version, string Url, string Name);

/// <summary>Pure logic of the update check (the HTTP call lives in the app).</summary>
public static class UpdateCheck
{
    /// <summary>
    /// Parses the JSON of <c>GET /repos/{owner}/{repo}/releases/latest</c>. Drafts, pre-releases and tags that
    /// are not versions yield null.
    /// </summary>
    public static LatestRelease? ParseLatestRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Bool(root, "draft") || Bool(root, "prerelease"))
            {
                return null;
            }

            var tag = Str(root, "tag_name");
            var version = ReleaseVersion.TryParse(tag);
            if (tag is null || version is null)
            {
                return null;
            }

            var url = Str(root, "html_url");
            if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = AppInfo.RepositoryUrl + "/releases/latest";
            }

            return new LatestRelease(tag, version, url, Str(root, "name") ?? tag);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="latest"/> is newer than the running <paramref name="current"/> version.
    /// A current version that cannot be parsed never reports an update.
    /// </summary>
    public static bool IsNewer(ReleaseVersion latest, string current)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var cur = ReleaseVersion.TryParse(current);
        return cur is not null && latest > cur;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
