using System.Text.RegularExpressions;
using ClickZen.Core.Devices;

namespace ClickZen.Platform.Windows;

/// <summary>Finds the window an <see cref="EmulatorProfile"/> refers to. Pure functions over enumerated windows.</summary>
public static class WindowMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>True when every non-empty criterion of <paramref name="rule"/> matches. An empty or invalid rule matches nothing.</summary>
    public static bool IsMatch(WindowMatchRule rule, DesktopWindowInfo window) => Score(rule, window) > 0;

    /// <summary>
    /// The best window for the profile, or null. Among windows satisfying the rule, prefers
    /// (1) the one whose title equals the rule's title exactly, (2) <paramref name="preferredHandle"/>
    /// (e.g. the window bound last time), (3) the shortest title (the main window rather than "… - Settings").
    /// </summary>
    public static DesktopWindowInfo? FindBestMatch(EmulatorProfile profile, IEnumerable<DesktopWindowInfo> windows, nint preferredHandle = 0) =>
        FindBestMatch(profile.Match, windows, preferredHandle);

    public static DesktopWindowInfo? FindBestMatch(WindowMatchRule rule, IEnumerable<DesktopWindowInfo> windows, nint preferredHandle = 0)
    {
        DesktopWindowInfo? best = null;
        var bestScore = 0;
        foreach (var w in windows)
        {
            var s = Score(rule, w);
            if (s <= 0)
            {
                continue;
            }

            if (preferredHandle != 0 && w.Handle == preferredHandle)
            {
                s += 5;
            }

            if (s > bestScore || (s == bestScore && best is not null && w.Title.Length < best.Title.Length))
            {
                best = w;
                bestScore = s;
            }
        }

        return best;
    }

    /// <summary>All windows satisfying the rule, best first.</summary>
    public static IReadOnlyList<DesktopWindowInfo> FindMatches(WindowMatchRule rule, IEnumerable<DesktopWindowInfo> windows) =>
        windows.Select(w => (w, s: Score(rule, w)))
            .Where(t => t.s > 0)
            .OrderByDescending(t => t.s)
            .ThenBy(t => t.w.Title.Length)
            .Select(t => t.w)
            .ToList();

    /// <summary>
    /// Proposes a rule for a window the user picked: process name + class name + the title (contains).
    /// The UI may loosen it (e.g. drop the instance number from the title).
    /// </summary>
    public static WindowMatchRule SuggestRule(DesktopWindowInfo window) => new()
    {
        ProcessName = string.IsNullOrEmpty(window.ProcessName) ? null : window.ProcessName,
        ClassName = string.IsNullOrEmpty(window.ClassName) ? null : window.ClassName,
        Title = window.Title,
        TitleMode = TitleMatchMode.Contains,
    };

    /// <summary>0 = no match; otherwise higher is better.</summary>
    internal static int Score(WindowMatchRule rule, DesktopWindowInfo window)
    {
        if (rule.IsEmpty)
        {
            return 0;
        }

        var score = 1;
        if (!string.IsNullOrWhiteSpace(rule.ProcessName))
        {
            if (!string.Equals(WindowMatchRule.NormalizeProcessName(rule.ProcessName),
                    WindowMatchRule.NormalizeProcessName(window.ProcessName), StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(rule.ClassName))
        {
            if (!string.Equals(rule.ClassName.Trim(), window.ClassName, StringComparison.Ordinal))
            {
                return 0;
            }

            score += 2;
        }

        if (!string.IsNullOrEmpty(rule.Title))
        {
            if (rule.TitleMode == TitleMatchMode.Regex)
            {
                try
                {
                    if (!Regex.IsMatch(window.Title, rule.Title, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                    {
                        return 0;
                    }
                }
                catch (ArgumentException)
                {
                    return 0; // invalid pattern
                }
                catch (RegexMatchTimeoutException)
                {
                    return 0;
                }

                score += 2;
            }
            else
            {
                if (!window.Title.Contains(rule.Title, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                score += string.Equals(window.Title, rule.Title, StringComparison.OrdinalIgnoreCase) ? 10 : 2;
            }
        }

        return score;
    }
}
