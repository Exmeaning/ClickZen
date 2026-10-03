using System.Text.RegularExpressions;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Devices;

/// <summary>Which API grabs the emulator window's pixels.</summary>
public enum WindowCaptureMethod
{
    /// <summary>Windows.Graphics.Capture when available, otherwise PrintWindow.</summary>
    Auto,
    /// <summary>Windows.Graphics.Capture only (works for occluded windows, GPU-rendered content).</summary>
    Wgc,
    /// <summary>PrintWindow(PW_RENDERFULLCONTENT) polling; slower, for systems/windows where WGC fails.</summary>
    PrintWindow,
}

/// <summary>How <see cref="WindowMatchRule.Title"/> is compared with the window title.</summary>
public enum TitleMatchMode
{
    /// <summary>Case-insensitive substring.</summary>
    Contains,
    /// <summary>.NET regular expression (case-insensitive).</summary>
    Regex,
}

/// <summary>
/// Identifies the emulator's top-level window. Every non-empty criterion must match;
/// a rule without any criterion matches nothing.
/// </summary>
public sealed class WindowMatchRule
{
    /// <summary>Executable name without path; ".exe" is optional and the comparison case-insensitive. Optional.</summary>
    public string? ProcessName { get; set; }

    /// <summary>Exact Win32 window class name (e.g. "Qt5156QWindowIcon"). Optional.</summary>
    public string? ClassName { get; set; }

    /// <summary>Substring or regular expression (see <see cref="TitleMode"/>). Optional.</summary>
    public string? Title { get; set; }

    public TitleMatchMode TitleMode { get; set; } = TitleMatchMode.Contains;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ProcessName) && string.IsNullOrWhiteSpace(ClassName) && string.IsNullOrEmpty(Title);

    /// <summary>Null when the rule is usable, otherwise a short English reason (empty rule, bad regex).</summary>
    public string? Validate()
    {
        if (IsEmpty)
        {
            return "At least one of process name, class name or title is required.";
        }

        if (TitleMode == TitleMatchMode.Regex && !string.IsNullOrEmpty(Title))
        {
            try
            {
                _ = new Regex(Title, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        return null;
    }

    /// <summary>Strips directories and a trailing ".exe" so "C:\x\MuMuPlayer.exe", "MuMuPlayer.exe" and "MuMuPlayer" compare equal.</summary>
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var n = name.Trim();
        var slash = n.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            n = n[(slash + 1)..];
        }

        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    public WindowMatchRule Clone() => (WindowMatchRule)MemberwiseClone();
}

/// <summary>
/// A bound emulator window ("模拟器档案"), persisted in emulator-profiles.json.
/// <para>Coordinate spaces: <see cref="CropRect"/> is in the window's <b>client area, physical pixels</b>
/// (origin = top-left of the client area). The captured frame is exactly the crop, so a frame pixel
/// <c>p</c> is client pixel <c>CropRect.Location + p</c>. Frame pixels map to device (adb input) pixels
/// with <see cref="CreateFrameMapping"/> (plain scaling to <see cref="ReferenceSize"/>).</para>
/// </summary>
public sealed class EmulatorProfile
{
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "";

    public WindowMatchRule Match { get; set; } = new();

    /// <summary>Region of the client area showing the Android screen; null = the whole client area.</summary>
    public RectI? CropRect { get; set; }

    /// <summary>Client area size (physical px) when <see cref="CropRect"/> was chosen. Lets the UI warn when
    /// the window was resized since; empty if unknown.</summary>
    public SizeI ClientSize { get; set; }

    /// <summary>The Android device resolution the cropped image represents (from adb, or entered by hand).
    /// Empty = use frame pixels as device pixels.</summary>
    public SizeI ReferenceSize { get; set; }

    /// <summary>adb serial (e.g. "127.0.0.1:16384") used for input; null = capture only.</summary>
    public string? AdbSerial { get; set; }

    public WindowCaptureMethod CaptureMethod { get; set; } = WindowCaptureMethod.Auto;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Frame (cropped capture) ↔ device mapping for a frame of <paramref name="frameSize"/>.</summary>
    public FrameToDevice CreateFrameMapping(SizeI frameSize) =>
        new(frameSize, ReferenceSize.IsEmpty ? frameSize : ReferenceSize);

    /// <summary>True when the window's client area no longer has the size the crop was chosen on.</summary>
    public bool ClientSizeChanged(SizeI currentClientSize) =>
        !ClientSize.IsEmpty && !currentClientSize.IsEmpty && ClientSize != currentClientSize;

    public EmulatorProfile Clone()
    {
        var copy = (EmulatorProfile)MemberwiseClone();
        copy.Match = Match.Clone();
        return copy;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Root document of %LocalAppData%\ClickZen\emulator-profiles.json.</summary>
public sealed class EmulatorProfilesDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public List<EmulatorProfile> Profiles { get; set; } = [];
}
