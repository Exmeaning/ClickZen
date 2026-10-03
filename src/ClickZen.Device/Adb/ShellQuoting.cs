namespace ClickZen.Device.Adb;

/// <summary>How root commands are wrapped on a particular device.</summary>
public enum SuStyle
{
    /// <summary><c>su -c '&lt;cmd&gt;'</c> – Magisk, KernelSU, SuperSU, most emulators.</summary>
    DashC,
    /// <summary><c>su 0 sh -c '&lt;cmd&gt;'</c> – AOSP userdebug/eng <c>su</c> (Android Studio emulator images).</summary>
    Uid0,
}

/// <summary>POSIX shell quoting helpers for commands sent through <c>adb shell</c>.</summary>
public static class ShellQuoting
{
    /// <summary>
    /// Wraps <paramref name="value"/> in single quotes so that <c>sh</c> treats it as one literal word.
    /// Embedded single quotes become <c>'\''</c>.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Builds the shell line that runs <paramref name="command"/> as root.</summary>
    public static string BuildRootCommand(string command, SuStyle style = SuStyle.DashC)
    {
        ArgumentNullException.ThrowIfNull(command);
        return style switch
        {
            SuStyle.Uid0 => "su 0 sh -c " + Quote(command),
            _ => "su -c " + Quote(command),
        };
    }
}
