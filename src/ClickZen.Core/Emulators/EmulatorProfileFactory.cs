using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Emulators;

/// <summary>Builds <see cref="EmulatorProfile"/>s for detected emulator instances (one-click window binding).</summary>
public static class EmulatorProfileFactory
{
    /// <summary>
    /// The match rule for an instance's window: process name and window class identify the emulator, the title
    /// tells instances apart. AVD titles carry the console port (":5554"), which changes when the port does, so for
    /// the Android Studio emulator the rule matches on the AVD name only. Qt window classes embed the Qt version
    /// ("Qt653QWindowIcon"), which changes with emulator updates, so Qt classes are left out.
    /// </summary>
    public static WindowMatchRule MatchRule(EmulatorInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var title = instance.WindowTitle.Trim();
        if (instance.Kind == EmulatorKind.AndroidStudio)
        {
            var (avd, _) = EmulatorConfigParsers.AvdWindowTitle(title);
            title = avd ?? instance.InstanceKey;
        }

        var cls = instance.WindowClass.Trim();
        var versionedQt = cls.StartsWith("Qt", StringComparison.Ordinal) && cls.EndsWith("QWindowIcon", StringComparison.Ordinal);
        return new WindowMatchRule
        {
            ProcessName = string.IsNullOrWhiteSpace(instance.ProcessName) ? null : instance.ProcessName,
            ClassName = cls.Length == 0 || versionedQt ? null : cls,
            Title = title.Length == 0 ? null : title,
            TitleMode = TitleMatchMode.Contains,
        };
    }

    /// <summary>
    /// A profile for <paramref name="instance"/>: its name, a match rule for its window, the screen area
    /// (<paramref name="crop"/>, null = whole client area), the client size it was chosen on, the device resolution
    /// and the linked adb serial. When <paramref name="existing"/> is given (same instance bound before) its id and
    /// capture method are kept so the window device is updated in place.
    /// </summary>
    public static EmulatorProfile Create(
        EmulatorInstance instance,
        string displayName,
        RectI? crop,
        SizeI clientSize,
        SizeI referenceSize,
        string? adbSerial,
        EmulatorProfile? existing = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var p = existing?.Clone() ?? new EmulatorProfile();
        p.Name = string.IsNullOrWhiteSpace(displayName) ? instance.Name : displayName.Trim();
        p.Match = MatchRule(instance);
        p.CropRect = crop;
        p.ClientSize = clientSize;
        p.ReferenceSize = referenceSize;
        p.AdbSerial = string.IsNullOrWhiteSpace(adbSerial) ? null : adbSerial.Trim();
        p.EmulatorKey = instance.Key;
        return p;
    }

    /// <summary>The saved profile created for <paramref name="instance"/> before, if any.</summary>
    public static EmulatorProfile? FindExisting(EmulatorInstance instance, IEnumerable<EmulatorProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.FirstOrDefault(p => string.Equals(p.EmulatorKey, instance.Key, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Orientation helpers shared by the binding wizard and one-click binding.</summary>
public static class ScreenOrientation
{
    /// <summary>
    /// <paramref name="size"/> transposed when <paramref name="picture"/> has the other orientation (portrait vs.
    /// landscape); unchanged when either is square or empty. "wm size" reports the natural orientation, while the
    /// emulator window shows the current one.
    /// </summary>
    public static SizeI OrientLike(SizeI size, SizeI picture) =>
        !picture.IsEmpty && picture.Width != picture.Height && size.Width != size.Height && picture.IsLandscape != size.IsLandscape
            ? size.Transposed
            : size;

    /// <summary>Relative difference of the aspect ratios of two sizes (0 = identical, 0.03 = 3 %); infinity for empty sizes.</summary>
    public static double AspectDifference(SizeI a, SizeI b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return double.PositiveInfinity;
        }

        var ra = (double)a.Width / a.Height;
        var rb = (double)b.Width / b.Height;
        return Math.Abs(ra - rb) / rb;
    }
}
