using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ClickZen.TestDiagnostics;

internal static class CiFailureReporter
{
    private static int _count;

    [ModuleInitializer]
    internal static void Init()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var ex = e.Exception;
            var ns = ex.GetType().Namespace ?? "";
            var isXunitFailure = ns.StartsWith("Xunit.Sdk", StringComparison.Ordinal)
                && !ex.GetType().Name.Contains("Skip", StringComparison.Ordinal);

            if (!isXunitFailure)
            {
                // Ignore routine expected exceptions tested via Assert.Throws*.
                if (ex is ArgumentException or InvalidOperationException or FormatException or IOException or OperationCanceledException)
                {
                    return;
                }

                var stCheck = new StackTrace(1, false).ToString();
                if (!stCheck.Contains(".Tests.", StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (Interlocked.Increment(ref _count) > 20)
            {
                return;
            }

            var st = new StackTrace(1, true);
            var frameInfo = "";
            foreach (var f in st.GetFrames())
            {
                var m = f.GetMethod();
                var decl = m?.DeclaringType?.FullName ?? "";
                if (decl.Contains(".Tests.", StringComparison.Ordinal))
                {
                    frameInfo = $"{decl}.{m?.Name}:{f.GetFileLineNumber()}";
                    break;
                }
            }

            var msg = ex.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal);
            var line = $"\n::error title={ex.GetType().Name}::{frameInfo} -> {msg}\n";
            Console.Out.WriteLine(line);
            Console.Error.WriteLine(line);
        };
    }
}
