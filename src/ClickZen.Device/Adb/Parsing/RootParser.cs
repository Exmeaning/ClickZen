using System.Text.RegularExpressions;

namespace ClickZen.Device.Adb.Parsing;

/// <summary>Outcome of a <c>su -c id</c> probe.</summary>
public enum RootStatus
{
    /// <summary>Output not recognised.</summary>
    Unknown,
    /// <summary><c>uid=0</c> – root shell available.</summary>
    Rooted,
    /// <summary>No <c>su</c> binary on the device.</summary>
    SuNotFound,
    /// <summary><c>su</c> exists but refused (user denied the prompt, or su is restricted to other uids).</summary>
    Denied,
    /// <summary>No answer in time – usually the root manager is still waiting for the user to grant access on the phone.</summary>
    Timeout,
}

/// <summary>Interprets the output of <c>id</c> run through <c>su</c>.</summary>
public static partial class RootParser
{
    /// <summary>True when the output of <c>id</c> reports <c>uid=0</c>.</summary>
    public static bool IsRoot(string? idOutput) =>
        !string.IsNullOrEmpty(idOutput) && Uid0Regex().IsMatch(idOutput);

    public static RootStatus Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return RootStatus.Unknown;
        }

        if (IsRoot(output))
        {
            return RootStatus.Rooted;
        }

        if (output.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("inaccessible", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return RootStatus.SuNotFound;
        }

        if (output.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("uid=", StringComparison.Ordinal))
        {
            // A non-zero uid means su ran but did not elevate (e.g. emulator su restricted to shell).
            return RootStatus.Denied;
        }

        return RootStatus.Unknown;
    }

    [GeneratedRegex(@"(?:^|\s)uid=0(?:\(|\s|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Uid0Regex();
}
