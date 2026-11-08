using AdvancedSharpAdbClient.Models;
using ClickZen.Core.Geometry;
using ClickZen.Device.Adb;
using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class DisplayFocusParserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Exact verbatim output reported in GitHub Issue #4 (MuMu 12 v6.4.6.0, 127.0.0.1:16416).</summary>
    private const string Issue4FilteredOutput = """
        mCurrentFocus=null
        mFocusedApp=null
        mCurrentFocus=null
        mFocusedApp=ActivityRecord{24e8c12 u0 app.lawnchair/.LawnchairLauncher t2}
        mCurrentFocus=Window{404da34 u0 com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity}
        mFocusedApp=ActivityRecord{1f470d5 u0 com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity t518}
        """;

    private const string FullMuMuMultiDisplayDumpsys = """
        WINDOW MANAGER LAST ANR (dumpsys window lastanr)
          Display: mDisplayId=0
            init=1600x900 240dpi cur=1600x900
            mCurrentFocus=Window{deadbeef u0 com.stale.crashed/.CrashActivity}
            mFocusedApp=ActivityRecord{deadbeef u0 com.stale.crashed/.CrashActivity t11}

        WINDOW MANAGER DISPLAY CONTENTS (dumpsys window displays)
          Display: mDisplayId=1 (organized)
            init=1600x900 240dpi cur=1600x900 app=1600x900
            mCurrentFocus=null
            mFocusedApp=null
          Display: mDisplayId=0 (organized)
            init=1600x900 240dpi cur=1600x900 app=1600x900
            mCurrentFocus=null
            mFocusedApp=ActivityRecord{24e8c12 u0 app.lawnchair/.LawnchairLauncher t2}
          Display: mDisplayId=2 (organized)
            init=900x1600 320dpi cur=761x1376 app=761x1376
            mCurrentFocus=Window{404da34 u0 com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity}
            mFocusedApp=ActivityRecord{1f470d5 u0 com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity t518}

        WINDOW MANAGER WINDOWS (dumpsys window windows)
          mGlobalConfiguration={1.0 ?mcc?mnc}
          mTopFocusedDisplayId=2
        """;

    [Fact]
    public void Issue4_filtered_dumpsys_resolves_foreground_game_over_launcher()
    {
        var active = DisplayFocusParser.Parse(Issue4FilteredOutput);

        Assert.Equal("com.lilithgame.pgame.android.cn", active.ForegroundPackage);
        Assert.Equal("com.farlightgames.igame.SplashActivity", active.ForegroundActivity);
        Assert.Equal("com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity", active.ForegroundComponent);
        Assert.Equal(3, active.Displays.Count);
    }

    [Fact]
    public void Full_mumu_multidisplay_dumpsys_skips_last_anr_and_selects_virtual_display()
    {
        var active = DisplayFocusParser.Parse(FullMuMuMultiDisplayDumpsys);

        Assert.Equal(2, active.DisplayId);
        Assert.Equal(new SizeI(761, 1376), active.Size);
        Assert.Equal(320, active.Density);
        Assert.Equal("com.lilithgame.pgame.android.cn", active.ForegroundPackage);
        Assert.Equal("com.farlightgames.igame.SplashActivity", active.ForegroundActivity);
    }

    [Fact]
    public void Virtual_display_with_game_wins_even_when_top_focus_briefly_points_to_display0_launcher()
    {
        var unfocusedHost = FullMuMuMultiDisplayDumpsys.Replace("mTopFocusedDisplayId=2", "mTopFocusedDisplayId=0", StringComparison.Ordinal);

        var active = DisplayFocusParser.Parse(unfocusedHost);

        Assert.Equal(2, active.DisplayId);
        Assert.Equal("com.lilithgame.pgame.android.cn", active.ForegroundPackage);
    }

    [Fact]
    public void Preferred_display_id_from_mumu_ipc_selects_active_tab_between_two_kept_alive_games()
    {
        const string twoGames = """
            WINDOW MANAGER DISPLAY CONTENTS (dumpsys window displays)
              Display: mDisplayId=0 (organized)
                init=1600x900 240dpi cur=1600x900
                mCurrentFocus=null
                mFocusedApp=ActivityRecord{111 u0 app.lawnchair/.LawnchairLauncher t2}
              Display: mDisplayId=2 (organized)
                init=1920x1080 320dpi cur=1920x1080
                mCurrentFocus=Window{222 u0 com.hypergryph.arknights/com.u8.sdk.U8UnityContext}
                mFocusedApp=ActivityRecord{223 u0 com.hypergryph.arknights/com.u8.sdk.U8UnityContext t10}
              Display: mDisplayId=4 (organized)
                init=900x1600 320dpi cur=900x1600
                mCurrentFocus=Window{404da34 u0 com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity}
                mFocusedApp=ActivityRecord{1f470d5 u0 com.lilithgame.pgame.android.cn/com.farlightgames.igame.SplashActivity t518}
            WINDOW MANAGER WINDOWS (dumpsys window windows)
              mTopFocusedDisplayId=2
            """;

        var tab4 = DisplayFocusParser.Parse(twoGames, preferredDisplayId: 4);
        Assert.Equal(4, tab4.DisplayId);
        Assert.Equal("com.lilithgame.pgame.android.cn", tab4.ForegroundPackage);
        Assert.Equal(new SizeI(900, 1600), tab4.Size);

        var tab2 = DisplayFocusParser.Parse(twoGames, preferredDisplayId: 2);
        Assert.Equal(2, tab2.DisplayId);
        Assert.Equal("com.hypergryph.arknights", tab2.ForegroundPackage);
    }

    [Fact]
    public void System_window_in_current_focus_falls_back_to_focused_app_on_same_display()
    {
        const string notificationShade = """
            WINDOW MANAGER DISPLAY CONTENTS (dumpsys window displays)
              Display: mDisplayId=0
                init=1080x2400 420dpi cur=1080x2400
                mCurrentFocus=Window{9f812 u0 NotificationShade}
                mFocusedApp=ActivityRecord{abc12 u0 com.android.settings/.Settings t9}
            """;

        var active = DisplayFocusParser.Parse(notificationShade);
        Assert.Equal(0, active.DisplayId);
        Assert.Equal("com.android.settings", active.ForegroundPackage);
        Assert.Equal(".Settings", active.ForegroundActivity);
    }

    [Fact]
    public async Task AdbService_GetActiveDisplayAsync_parses_dumpsys_window()
    {
        var (client, fake) = FakeAdbClient.Create();
        using var svc = new AdbService(client);
        fake.Devices.Add(new DeviceData { Serial = "127.0.0.1:16416", State = DeviceState.Online });
        fake.Shell = (_, cmd, _) => Task.FromResult<IEnumerable<string>>(
            cmd == "dumpsys window"
                ? FullMuMuMultiDisplayDumpsys.Replace("\r", "", StringComparison.Ordinal).Split('\n')
                : []);

        var active = await svc.GetActiveDisplayAsync("127.0.0.1:16416", preferredDisplayId: null, Ct);

        Assert.Equal(2, active.DisplayId);
        Assert.Equal("com.lilithgame.pgame.android.cn", active.ForegroundPackage);
        Assert.Equal(new SizeI(761, 1376), active.Size);
    }
}
