using System.Globalization;
using System.Text;

namespace ClickZen.Core.Emulators;

/// <summary>Splits Windows command lines (CommandLineToArgvW rules) and reads emulator-style options from them.</summary>
public static class CommandLine
{
    /// <summary>
    /// Splits a command line the way the C runtime does: whitespace separates arguments, double quotes group,
    /// 2n backslashes before a quote give n backslashes and toggle quoting, 2n+1 give n backslashes and a literal quote.
    /// </summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return args;
        }

        var s = commandLine;
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\')
            {
                var start = i;
                while (i < s.Length && s[i] == '\\')
                {
                    i++;
                }

                var count = i - start;
                if (i < s.Length && s[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    hasToken = true;
                }
                else
                {
                    current.Append('\\', count);
                    hasToken = true;
                    i--;
                }

                continue;
            }

            if (c == '"')
            {
                // "" inside quotes is a literal quote (CRT behaviour).
                if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>
    /// Value of an option written as "<paramref name="name"/> value", "<paramref name="name"/>=value" or
    /// "<paramref name="name"/>:value" (case-insensitive name), or null.
    /// </summary>
    public static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Count ? args[i + 1] : null;
            }

            if (a.Length > name.Length + 1 && a.StartsWith(name, StringComparison.OrdinalIgnoreCase) && a[name.Length] is '=' or ':')
            {
                return a[(name.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>The first value of any of <paramref name="names"/> (see <see cref="Option"/>).</summary>
    public static string? FirstOption(IReadOnlyList<string> args, params string[] names) =>
        names.Select(n => Option(args, n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Integer value of <paramref name="name"/>, or null.</summary>
    public static int? IntOption(IReadOnlyList<string> args, string name) =>
        int.TryParse(Option(args, name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}
