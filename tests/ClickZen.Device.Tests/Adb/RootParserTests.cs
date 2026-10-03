using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class RootParserTests
{
    [Theory]
    [InlineData("uid=0(root) gid=0(root) groups=0(root) context=u:r:magisk:s0")]
    [InlineData("uid=0(root) gid=0(root)")]
    [InlineData("uid=0 gid=0")]
    [InlineData("\nuid=0(root) gid=0(root) groups=0(root),1004(input) context=u:r:su:s0\n")]
    public void Detects_root(string output)
    {
        Assert.True(RootParser.IsRoot(output));
        Assert.Equal(RootStatus.Rooted, RootParser.Classify(output));
    }

    [Theory]
    [InlineData("uid=2000(shell) gid=2000(shell) groups=2000(shell),1004(input)", RootStatus.Denied)]
    [InlineData("uid=10(x) gid=0(root)", RootStatus.Denied)]
    [InlineData("/system/bin/sh: su: inaccessible or not found", RootStatus.SuNotFound)]
    [InlineData("/system/bin/sh: su: not found", RootStatus.SuNotFound)]
    [InlineData("Permission denied", RootStatus.Denied)]
    [InlineData("su: permission denied", RootStatus.Denied)]
    [InlineData("", RootStatus.Unknown)]
    [InlineData("whatever", RootStatus.Unknown)]
    public void Non_root(string output, RootStatus expected)
    {
        Assert.False(RootParser.IsRoot(output));
        Assert.Equal(expected, RootParser.Classify(output));
    }

    [Fact]
    public void Uid_00_or_uid_01_is_not_root()
    {
        Assert.False(RootParser.IsRoot("uid=01(x)"));
        Assert.False(RootParser.IsRoot("euid=0"));
    }
}

public sealed class ShellQuotingTests
{
    [Theory]
    [InlineData("id", "'id'")]
    [InlineData("", "''")]
    [InlineData("echo 'hi'", @"'echo '\''hi'\'''")]
    [InlineData("a \"b\" $c `d`", "'a \"b\" $c `d`'")]
    public void Quote(string input, string expected)
    {
        Assert.Equal(expected, ShellQuoting.Quote(input));
    }

    [Fact]
    public void Root_command_styles()
    {
        Assert.Equal("su -c 'id'", ShellQuoting.BuildRootCommand("id"));
        Assert.Equal("su 0 sh -c 'id'", ShellQuoting.BuildRootCommand("id", SuStyle.Uid0));
        Assert.Equal(@"su -c 'echo '\''x y'\'' > /data/t'", ShellQuoting.BuildRootCommand("echo 'x y' > /data/t"));
    }
}
