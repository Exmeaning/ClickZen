using System.Globalization;
using System.Text.RegularExpressions;
using ClickZen.Core.Geometry;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>An Android <c>package/activity</c> component parsed from <c>dumpsys window</c>.</summary>
public sealed record AppComponent(string Package, string? Activity)
{
    public string FullName => string.IsNullOrEmpty(Activity) ? Package : $"{Package}/{Activity}";

    /// <summary>True when this component is a known home screen / launcher rather than a user app or game.</summary>
    public bool IsLauncher => DisplayFocusParser.IsLauncherComponent(Package, Activity);
}

/// <summary>Focus and geometry state of one Android display from <c>dumpsys window</c>.</summary>
public sealed record DisplayFocusEntry(
    int DisplayId,
    SizeI Size,
    int Density,
    AppComponent? CurrentFocus,
    AppComponent? FocusedApp)
{
    /// <summary>The focused window's app when it names a package, otherwise the display's focused ActivityRecord.</summary>
    public AppComponent? EffectiveApp => CurrentFocus ?? FocusedApp;

    public bool HasNonLauncherFocus => CurrentFocus is { IsLauncher: false };

    public bool HasNonLauncherApp => EffectiveApp is { IsLauncher: false };
}

/// <summary>Resolved active display and foreground application of an Android device.</summary>
public sealed record ActiveDisplayInfo(
    int DisplayId,
    SizeI Size,
    int Density,
    AppComponent? ForegroundApp,
    IReadOnlyList<DisplayFocusEntry> Displays)
{
    public static ActiveDisplayInfo Default { get; } = new(0, default, 0, null, []);

    public string? ForegroundPackage => ForegroundApp?.Package;

    public string? ForegroundActivity => ForegroundApp?.Activity;

    public string? ForegroundComponent => ForegroundApp?.FullName;
}

/// <summary>
/// Parses <c>dumpsys window</c> to find the active display and foreground application, including multi-display
/// emulators such as MuMu 12 (v5/v6 <c>app_keptlive</c> / multi-tab mode) where games run on a secondary virtual
/// display (<c>displayId &gt; 0</c>) while <c>displayId = 0</c> stays on the Android launcher.
/// </summary>
public static partial class DisplayFocusParser
{
    private const string DisplayContentsHeader = "WINDOW MANAGER DISPLAY CONTENTS";
    private const string SectionHeaderPrefix = "WINDOW MANAGER ";

    private static readonly HashSet<string> KnownLauncherPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "app.lawnchair",
        "com.mumu.launcher",
        "com.netease.mumu.cloner",
        "com.android.launcher",
        "com.android.launcher2",
        "com.android.launcher3",
        "com.google.android.apps.nexuslauncher",
        "com.cyanogenmod.trebuchet",
        "org.lineageos.trebuchet",
        "com.ldmnq.launcher3",
        "com.vphone.launcher",
        "com.bluestacks.appmart",
        "com.bluestacks.gamecenter",
        "com.microvirt.launcher2",
        "com.bignox.app.noxlauncher",
        "com.sec.android.app.launcher",
        "com.miui.home",
        "com.huawei.android.launcher",
        "com.oppo.launcher",
        "com.bbk.launcher2",
    };

    /// <summary>True when <paramref name="package"/> / <paramref name="activity"/> is an Android home launcher.</summary>
    public static bool IsLauncherComponent(string? package, string? activity = null)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return false;
        }

        if (KnownLauncherPackages.Contains(package))
        {
            return true;
        }

        if (package.EndsWith(".launcher", StringComparison.OrdinalIgnoreCase)
            || package.EndsWith(".launcher2", StringComparison.OrdinalIgnoreCase)
            || package.EndsWith(".launcher3", StringComparison.OrdinalIgnoreCase)
            || package.EndsWith(".home", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(activity))
        {
            var simple = activity.LastIndexOf('.') is >= 0 and var dot ? activity[(dot + 1)..] : activity;
            if (simple.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase)
                || simple.EndsWith("LauncherActivity", StringComparison.OrdinalIgnoreCase)
                || simple.Equals("HomeActivity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <c>dumpsys window</c> (or <c>dumpsys window displays</c>, or filtered <c>mCurrentFocus</c>/<c>mFocusedApp</c>
    /// lines) and resolves the active display and foreground application.
    /// </summary>
    /// <param name="dumpsysWindow">Output of <c>dumpsys window</c>.</param>
    /// <param name="preferredDisplayId">Optional display id reported by emulator host IPC (e.g. MuMu <c>external_renderer_ipc</c>).</param>
    public static ActiveDisplayInfo Parse(string? dumpsysWindow, int? preferredDisplayId = null)
    {
        if (string.IsNullOrWhiteSpace(dumpsysWindow))
        {
            return ActiveDisplayInfo.Default;
        }

        // Skip any earlier "WINDOW MANAGER LAST ANR" snapshot that precedes live display contents.
        var lastHeader = dumpsysWindow.LastIndexOf(DisplayContentsHeader, StringComparison.OrdinalIgnoreCase);
        var live = lastHeader >= 0 ? dumpsysWindow.AsSpan(lastHeader) : dumpsysWindow.AsSpan();

        int? topFocusedDisplayId = null;
        var displays = new List<MutableDisplay>();
        MutableDisplay? current = null;
        var hasExplicitDisplayHeaders = false;
        var inDisplayContents = lastHeader >= 0;
        var syntheticDisplayId = 0;

        foreach (var rawLine in live.EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (line.StartsWith(SectionHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                inDisplayContents = line.StartsWith(DisplayContentsHeader, StringComparison.OrdinalIgnoreCase);
                if (!inDisplayContents)
                {
                    current = null;
                }

                continue;
            }

            var lineStr = line.ToString();

            if (TryParseIntAfter(lineStr, "mTopFocusedDisplayId=", out var topId))
            {
                topFocusedDisplayId = topId;
                continue;
            }

            if (TryParseDisplayHeader(lineStr, out var displayId))
            {
                hasExplicitDisplayHeaders = true;
                current = new MutableDisplay(displayId);
                displays.Add(current);
                continue;
            }

            if (current is not null && current.Size.IsEmpty && lineStr.Contains("init=", StringComparison.Ordinal))
            {
                ParseDisplayGeometry(lineStr, current);
                continue;
            }

            if (lineStr.StartsWith("mCurrentFocus=", StringComparison.Ordinal))
            {
                if (!hasExplicitDisplayHeaders && (!inDisplayContents || lastHeader < 0))
                {
                    // Headerless / grep-filtered output: pair each mCurrentFocus + mFocusedApp group sequentially.
                    if (current is null || current.SawCurrentFocus)
                    {
                        current = new MutableDisplay(syntheticDisplayId++);
                        displays.Add(current);
                    }
                }

                if (current is not null)
                {
                    current.SawCurrentFocus = true;
                    current.CurrentFocus = ParseComponentFromToken(lineStr["mCurrentFocus=".Length..]);
                }

                continue;
            }

            if (lineStr.StartsWith("mFocusedApp=", StringComparison.Ordinal))
            {
                if (!hasExplicitDisplayHeaders && (!inDisplayContents || lastHeader < 0))
                {
                    if (current is null || current.SawFocusedApp)
                    {
                        current = new MutableDisplay(syntheticDisplayId++);
                        displays.Add(current);
                    }
                }

                if (current is not null)
                {
                    current.SawFocusedApp = true;
                    current.FocusedApp = ParseComponentFromToken(lineStr["mFocusedApp=".Length..]);
                }
            }
        }

        if (displays.Count == 0)
        {
            return ActiveDisplayInfo.Default;
        }

        // Deduplicate by DisplayId (keep the first occurrence in live display contents, merging missing fields if any).
        var entries = new List<DisplayFocusEntry>(displays.Count);
        var byId = new Dictionary<int, int>();
        foreach (var d in displays)
        {
            var entry = new DisplayFocusEntry(d.DisplayId, d.Size, d.Density, d.CurrentFocus, d.FocusedApp);
            if (byId.TryGetValue(d.DisplayId, out var existingIdx))
            {
                var prev = entries[existingIdx];
                entries[existingIdx] = new DisplayFocusEntry(
                    prev.DisplayId,
                    prev.Size.IsEmpty ? entry.Size : prev.Size,
                    prev.Density > 0 ? prev.Density : entry.Density,
                    prev.CurrentFocus ?? entry.CurrentFocus,
                    prev.FocusedApp ?? entry.FocusedApp);
            }
            else
            {
                byId[d.DisplayId] = entries.Count;
                entries.Add(entry);
            }
        }

        var chosen = SelectActiveDisplay(entries, topFocusedDisplayId, preferredDisplayId, hasExplicitDisplayHeaders);
        var resolvedId = hasExplicitDisplayHeaders ? chosen.DisplayId : (preferredDisplayId ?? topFocusedDisplayId ?? 0);
        return new ActiveDisplayInfo(resolvedId, chosen.Size, chosen.Density, chosen.EffectiveApp, entries);
    }

    /// <summary>Parses a single <c>Window{…}</c> or <c>ActivityRecord{…}</c> value into an <see cref="AppComponent"/>.</summary>
    public static AppComponent? ParseComponentFromToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var m = ComponentRegex().Match(trimmed);
        if (!m.Success)
        {
            return null;
        }

        var pkg = m.Groups["pkg"].Value;
        var act = m.Groups["act"].Value;
        return string.IsNullOrEmpty(pkg) ? null : new AppComponent(pkg, string.IsNullOrEmpty(act) ? null : act);
    }

    private static DisplayFocusEntry SelectActiveDisplay(
        List<DisplayFocusEntry> entries,
        int? topFocusedDisplayId,
        int? preferredDisplayId,
        bool hasExplicitDisplayHeaders)
    {
        // 1. Host emulator IPC preference (e.g. MuMu frontmost tab display id), when that display has a running app
        //    or when it explicitly selects the primary display and no secondary virtual display holds a non-launcher game.
        if (preferredDisplayId is int pref
            && entries.FirstOrDefault(e => e.DisplayId == pref) is { } prefEntry
            && (prefEntry.HasNonLauncherApp || (pref == 0 && !entries.Any(e => e.DisplayId > 0 && e.HasNonLauncherFocus))))
        {
            return prefEntry;
        }

        // 2. Android's mTopFocusedDisplayId when it points to a display with an active non-launcher window.
        if (topFocusedDisplayId is int topId
            && entries.FirstOrDefault(e => e.DisplayId == topId) is { } topEntry
            && topEntry.HasNonLauncherFocus)
        {
            return topEntry;
        }

        // 3. MuMu 5/6 app_keptlive / multi-tab virtual displays: a secondary display (DisplayId > 0) has a live
        //    non-launcher mCurrentFocus while Display 0 sits on the launcher (or has mCurrentFocus=null).
        var secondaryWithFocus = entries.Where(e => e.DisplayId > 0 && e.HasNonLauncherFocus).ToList();
        if (secondaryWithFocus.Count > 0)
        {
            // DisplayContents in AOSP dumpsys are emitted top-most/MRU first when organized; in headerless sequential
            // output the virtual display appears last.
            return hasExplicitDisplayHeaders ? secondaryWithFocus[0] : secondaryWithFocus[^1];
        }

        // 4. Any display (including Display 0) with a non-launcher mCurrentFocus.
        var anyWithFocus = entries.Where(e => e.HasNonLauncherFocus).ToList();
        if (anyWithFocus.Count > 0)
        {
            return hasExplicitDisplayHeaders ? anyWithFocus[0] : anyWithFocus[^1];
        }

        // 5. Secondary or primary display with a non-launcher mFocusedApp (e.g. during splash transition while mCurrentFocus is null).
        if (topFocusedDisplayId is int topAppId
            && entries.FirstOrDefault(e => e.DisplayId == topAppId) is { HasNonLauncherApp: true } topAppEntry)
        {
            return topAppEntry;
        }

        var secondaryWithApp = entries.Where(e => e.DisplayId > 0 && e.HasNonLauncherApp).ToList();
        if (secondaryWithApp.Count > 0)
        {
            return hasExplicitDisplayHeaders ? secondaryWithApp[0] : secondaryWithApp[^1];
        }

        var anyWithApp = entries.Where(e => e.HasNonLauncherApp).ToList();
        if (anyWithApp.Count > 0)
        {
            return hasExplicitDisplayHeaders ? anyWithApp[0] : anyWithApp[^1];
        }

        // 6. Fallback: preferred -> mTopFocusedDisplayId -> Display 0 -> last entry with any app -> first entry.
        if (preferredDisplayId is int prefFallback && entries.FirstOrDefault(e => e.DisplayId == prefFallback) is { } pf)
        {
            return pf;
        }

        if (topFocusedDisplayId is int topFallback && entries.FirstOrDefault(e => e.DisplayId == topFallback) is { } tf)
        {
            return tf;
        }

        return entries.FirstOrDefault(e => e.DisplayId == 0 && e.EffectiveApp is not null)
            ?? entries.LastOrDefault(e => e.EffectiveApp is not null)
            ?? entries.FirstOrDefault(e => e.DisplayId == 0)
            ?? entries[0];
    }

    private static bool TryParseDisplayHeader(string line, out int displayId)
    {
        displayId = 0;
        var m = DisplayHeaderRegex().Match(line);
        return m.Success && int.TryParse(m.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out displayId);
    }

    private static bool TryParseIntAfter(string line, string prefix, out int value)
    {
        value = 0;
        var idx = line.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }

        var span = line.AsSpan(idx + prefix.Length).TrimStart();
        var len = 0;
        if (len < span.Length && span[len] == '-')
        {
            len++;
        }

        while (len < span.Length && char.IsAsciiDigit(span[len]))
        {
            len++;
        }

        return len > 0 && int.TryParse(span[..len], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static void ParseDisplayGeometry(string line, MutableDisplay target)
    {
        SizeI initSize = default, curSize = default;
        var initMatch = InitGeometryRegex().Match(line);
        if (initMatch.Success
            && int.TryParse(initMatch.Groups["w"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var iw)
            && int.TryParse(initMatch.Groups["h"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var ih)
            && iw > 0 && ih > 0)
        {
            initSize = new SizeI(iw, ih);
            if (initMatch.Groups["dpi"].Success
                && int.TryParse(initMatch.Groups["dpi"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var dpi)
                && dpi > 0)
            {
                target.Density = dpi;
            }
        }

        var curMatch = CurGeometryRegex().Match(line);
        if (curMatch.Success
            && int.TryParse(curMatch.Groups["w"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var cw)
            && int.TryParse(curMatch.Groups["h"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var ch)
            && cw > 0 && ch > 0)
        {
            curSize = new SizeI(cw, ch);
        }

        target.Size = !curSize.IsEmpty ? curSize : initSize;
    }

    private sealed class MutableDisplay(int displayId)
    {
        public int DisplayId { get; } = displayId;

        public SizeI Size { get; set; }

        public int Density { get; set; }

        public AppComponent? CurrentFocus { get; set; }

        public AppComponent? FocusedApp { get; set; }

        public bool SawCurrentFocus { get; set; }

        public bool SawFocusedApp { get; set; }
    }

    [GeneratedRegex(@"^(?:Display:\s*mDisplayId=|Display\s*#?\s*)(?<id>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayHeaderRegex();

    [GeneratedRegex(@"\binit=(?<w>\d+)x(?<h>\d+)(?:\s+(?<dpi>\d+)dpi)?", RegexOptions.CultureInvariant)]
    private static partial Regex InitGeometryRegex();

    [GeneratedRegex(@"\bcur=(?<w>\d+)x(?<h>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CurGeometryRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(?<pkg>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)/(?<act>\.?[A-Za-z0-9_$.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentRegex();
}
