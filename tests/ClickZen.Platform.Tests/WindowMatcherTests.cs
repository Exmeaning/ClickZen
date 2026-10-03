using ClickZen.Core.Devices;
using ClickZen.Platform.Windows;

namespace ClickZen.Platform.Tests;

public sealed class WindowMatcherTests
{
    private static readonly DesktopWindowInfo MuMuMain = new(1, "MuMu模拟器12", "Qt5156QWindowIcon", 100, "MuMuPlayer");
    private static readonly DesktopWindowInfo MuMuSecond = new(2, "MuMu模拟器12-1", "Qt5156QWindowIcon", 101, "MuMuPlayer");
    private static readonly DesktopWindowInfo MuMuSettings = new(3, "MuMu模拟器12 - 设置中心", "Qt5156QWindowIcon", 100, "MuMuPlayer");
    private static readonly DesktopWindowInfo Notepad = new(4, "无标题 - 记事本", "Notepad", 200, "Notepad");
    private static readonly DesktopWindowInfo Avd = new(5, "Android Emulator - Pixel_API_36:5554", "Qt672QWindowIcon", 300, "qemu-system-x86_64");
    private static readonly DesktopWindowInfo[] All = [MuMuSettings, MuMuSecond, Notepad, MuMuMain, Avd];

    [Fact]
    public void Empty_rule_matches_nothing()
    {
        Assert.Null(WindowMatcher.FindBestMatch(new WindowMatchRule(), All));
        Assert.NotNull(new WindowMatchRule().Validate());
    }

    [Fact]
    public void Contains_title_prefers_exact_then_shortest()
    {
        var rule = new WindowMatchRule { Title = "mumu模拟器12" };
        Assert.Equal(MuMuMain, WindowMatcher.FindBestMatch(rule, All));

        var loose = new WindowMatchRule { Title = "MuMu" };
        Assert.Equal(MuMuMain, WindowMatcher.FindBestMatch(loose, All)); // shortest title wins
        Assert.Equal(3, WindowMatcher.FindMatches(loose, All).Count);
    }

    [Fact]
    public void Preferred_handle_breaks_ties()
    {
        var rule = new WindowMatchRule { ProcessName = "MuMuPlayer", Title = "MuMu" };
        Assert.Equal(MuMuSecond, WindowMatcher.FindBestMatch(rule, All, preferredHandle: MuMuSecond.Handle));
    }

    [Fact]
    public void Regex_title()
    {
        var rule = new WindowMatchRule { Title = @"^MuMu模拟器12-\d+$", TitleMode = TitleMatchMode.Regex };
        Assert.Equal(MuMuSecond, WindowMatcher.FindBestMatch(rule, All));

        var avd = new WindowMatchRule { Title = @"Android Emulator - .*:5554", TitleMode = TitleMatchMode.Regex };
        Assert.Equal(Avd, WindowMatcher.FindBestMatch(avd, All));
    }

    [Fact]
    public void Invalid_regex_matches_nothing_and_fails_validation()
    {
        var rule = new WindowMatchRule { Title = "([", TitleMode = TitleMatchMode.Regex };
        Assert.Null(WindowMatcher.FindBestMatch(rule, All));
        Assert.NotNull(rule.Validate());
    }

    [Fact]
    public void Process_name_is_normalized()
    {
        foreach (var name in new[] { "notepad", "Notepad.exe", @"C:\Windows\System32\NOTEPAD.EXE" })
        {
            var rule = new WindowMatchRule { ProcessName = name };
            Assert.Equal(Notepad, WindowMatcher.FindBestMatch(rule, All));
        }
    }

    [Fact]
    public void All_criteria_must_match()
    {
        var rule = new WindowMatchRule { ProcessName = "MuMuPlayer", ClassName = "Notepad" };
        Assert.Null(WindowMatcher.FindBestMatch(rule, All));

        var cls = new WindowMatchRule { ClassName = "Qt672QWindowIcon" };
        Assert.Equal(Avd, WindowMatcher.FindBestMatch(cls, All));

        // Class names compare case-sensitively (Win32 class atoms are, in practice, exact strings).
        Assert.Null(WindowMatcher.FindBestMatch(new WindowMatchRule { ClassName = "notepad" }, All));
    }

    [Fact]
    public void Profile_overload_and_suggested_rule_round_trip()
    {
        var profile = new EmulatorProfile { Name = "MuMu #1", Match = WindowMatcher.SuggestRule(MuMuSecond) };
        Assert.Equal("MuMuPlayer", profile.Match.ProcessName);
        Assert.Equal(MuMuSecond, WindowMatcher.FindBestMatch(profile, All));
    }

    [Fact]
    public void Missing_process_name_does_not_match_a_process_rule()
    {
        var unknown = new DesktopWindowInfo(9, "Some window", "X", 1);
        Assert.False(WindowMatcher.IsMatch(new WindowMatchRule { ProcessName = "x" }, unknown));
        Assert.True(WindowMatcher.IsMatch(new WindowMatchRule { Title = "some" }, unknown));
    }
}
