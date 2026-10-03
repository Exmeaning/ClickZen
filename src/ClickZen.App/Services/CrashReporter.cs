using System.Runtime.InteropServices;
using System.Text;
using ClickZen.Core;
using ClickZen.Core.Persistence;
using ClickZen.App.Services.Logging;

namespace ClickZen.App.Services;

/// <summary>Writes crash reports with enough context to diagnose a problem after the fact.</summary>
public sealed class CrashReporter
{
    private readonly AppPaths _paths;
    private readonly InMemoryLogSink _logs;

    public CrashReporter(AppPaths paths, InMemoryLogSink logs)
    {
        _paths = paths;
        _logs = logs;
    }

    /// <returns>Path of the written report, or null if it could not be written.</returns>
    public string? Write(Exception exception, string origin)
    {
        try
        {
            Directory.CreateDirectory(_paths.CrashDirectory);
            var file = Path.Combine(_paths.CrashDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("=== ClickZen crash report ===");
            sb.AppendLine($"Time:        {DateTimeOffset.Now:O}");
            sb.AppendLine($"Origin:      {origin}");
            sb.AppendLine($"Version:     {AppInfo.Version}");
            sb.AppendLine($".NET:        {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"OS:          {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"Process:     {Environment.ProcessPath}");
            sb.AppendLine();
            sb.AppendLine("=== Exception ===");
            sb.AppendLine(exception.ToString());
            sb.AppendLine();
            sb.AppendLine("=== Recent log (newest last) ===");
            foreach (var e in _logs.Snapshot().TakeLast(200))
            {
                sb.Append(e.Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(' ').Append(e.Level.ToString()[..3].ToUpperInvariant())
                  .Append(' ').Append(e.Source)
                  .Append(": ").AppendLine(e.Message);
            }

            AtomicFile.WriteAllText(file, sb.ToString());
            return file;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
