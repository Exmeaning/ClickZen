using ClickZen.Core.Updates;

namespace ClickZen.Core.Tests.Updates;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v2.0.1", 2, 0, 1, "")]
    [InlineData("2.1", 2, 1, 0, "")]
    [InlineData("V3", 3, 0, 0, "")]
    [InlineData("2.0.0-dev", 2, 0, 0, "dev")]
    [InlineData("2.0.0-ci.42+abc", 2, 0, 0, "ci.42")]
    [InlineData("1.6.3.0", 1, 6, 3, "")]
    public void Parses_tags(string text, int major, int minor, int patch, string pre)
    {
        Assert.Equal(new ReleaseVersion(major, minor, patch, pre), ReleaseVersion.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v2.x")]
    [InlineData("2.0.0-")]
    [InlineData("-1.0")]
    public void Rejects_non_versions(string? text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Theory]
    [InlineData("2.0.0", "2.0.0-dev")] // pre-release ranks below the release
    [InlineData("2.0.0", "2.0.0-ci.17")]
    [InlineData("2.0.1", "2.0.0")]
    [InlineData("2.1.0", "2.0.9")]
    [InlineData("10.0.0", "9.9.9")]
    [InlineData("2.0.0-rc.2", "2.0.0-rc.1")]
    [InlineData("2.0.0-rc.10", "2.0.0-rc.9")]
    [InlineData("2.0.0-beta", "2.0.0-alpha")]
    [InlineData("2.0.0-alpha.1", "2.0.0-alpha")]
    [InlineData("2.0.0-alpha.beta", "2.0.0-alpha.1")]
    public void Orders_versions(string higher, string lower)
    {
        var h = ReleaseVersion.TryParse(higher)!;
        var l = ReleaseVersion.TryParse(lower)!;
        Assert.True(h > l);
        Assert.True(l < h);
        Assert.Equal(1, h.CompareTo(l));
        Assert.Equal(-1, Math.Sign(l.CompareTo(h)));
    }

    [Fact]
    public void Equal_versions_compare_equal_and_build_metadata_is_ignored()
    {
        var a = ReleaseVersion.TryParse("v2.0.0+build.1")!;
        var b = ReleaseVersion.TryParse("2.0.0")!;
        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("v2.0.0", "2.0.0-dev", true)]
    [InlineData("v2.0.0", "2.0.0", false)]
    [InlineData("v2.0.0", "2.0.1", false)]
    [InlineData("v2.3.0", "2.2.9", true)]
    [InlineData("v1.6.3", "2.0.0-dev", false)] // old Python release line never counts as an update
    [InlineData("v2.0.0", "garbage", false)]
    public void Detects_newer_release(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateCheck.IsNewer(ReleaseVersion.TryParse(latest)!, current));
    }

    [Fact]
    public void Parses_github_latest_release_json()
    {
        const string json = """
            {
              "html_url": "https://github.com/Exmeaning/ClickZen/releases/tag/v2.0.1",
              "tag_name": "v2.0.1",
              "name": "ClickZen 2.0.1",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """;
        var r = UpdateCheck.ParseLatestRelease(json);
        Assert.NotNull(r);
        Assert.Equal("v2.0.1", r.Tag);
        Assert.Equal(new ReleaseVersion(2, 0, 1, ""), r.Version);
        Assert.Equal("https://github.com/Exmeaning/ClickZen/releases/tag/v2.0.1", r.Url);
        Assert.Equal("ClickZen 2.0.1", r.Name);
    }

    [Theory]
    [InlineData("""{"tag_name":"v2.0.1","prerelease":true}""")]
    [InlineData("""{"tag_name":"v2.0.1","draft":true}""")]
    [InlineData("""{"tag_name":"nightly"}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""not json""")]
    public void Ignores_unusable_responses(string json) => Assert.Null(UpdateCheck.ParseLatestRelease(json));

    [Fact]
    public void Falls_back_to_repository_url_when_html_url_is_missing_or_unsafe()
    {
        var r = UpdateCheck.ParseLatestRelease("""{"tag_name":"v2.0.1","html_url":"javascript:alert(1)"}""");
        Assert.NotNull(r);
        Assert.Equal(AppInfo.RepositoryUrl + "/releases/latest", r.Url);
        Assert.Equal("v2.0.1", r.Name);
    }
}
