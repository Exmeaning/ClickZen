using ClickZen.Platform.Windows;
using Microsoft.Win32;

namespace ClickZen.Platform.Emulators;

/// <summary>
/// Everything emulator detection reads from the system. The real implementation is <see cref="SystemEmulatorEnvironment"/>;
/// tests substitute an in-memory fake so every emulator layout can be covered without the emulators installed.
/// </summary>
public interface IEmulatorEnvironment
{
    /// <summary>Running processes whose executable name (without ".exe") satisfies the filter.</summary>
    IReadOnlyList<ProcessInfo> FindProcesses(Func<string, bool> nameFilter);

    /// <summary>Visible top-level windows (title, class, owning process).</summary>
    IReadOnlyList<DesktopWindowInfo> GetWindows();

    /// <summary>Text of a file, or null when it does not exist or cannot be read (size-limited).</summary>
    string? ReadFile(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Full paths of the sub directories of <paramref name="path"/> (empty when missing).</summary>
    IReadOnlyList<string> GetDirectories(string path);

    /// <summary>Full paths of the files in <paramref name="path"/> matching <paramref name="pattern"/> (empty when missing).</summary>
    IReadOnlyList<string> GetFiles(string path, string pattern);

    /// <summary>A string value under HKLM (<paramref name="localMachine"/>) or HKCU, 64- and 32-bit views; null when absent.</summary>
    string? ReadRegistryString(bool localMachine, string subKey, string valueName);

    /// <summary>Names of the sub keys of HKLM/HKCU\<paramref name="subKey"/> in both registry views.</summary>
    IReadOnlyList<string> GetRegistrySubKeys(bool localMachine, string subKey);

    /// <summary>Expands environment variables such as %TEMP% and %LOCALAPPDATA%.</summary>
    string ExpandEnvironment(string text);
}

/// <summary>The real system: processes, files, registry and windows of this PC.</summary>
public sealed class SystemEmulatorEnvironment : IEmulatorEnvironment
{
    /// <summary>Emulator config files are small; anything larger is not what we are looking for.</summary>
    public const long MaxFileBytes = 4 * 1024 * 1024;

    private readonly uint _excludeProcessId;

    /// <param name="excludeProcessId">Our own process, whose windows are never emulator windows.</param>
    public SystemEmulatorEnvironment(uint excludeProcessId = 0) => _excludeProcessId = excludeProcessId;

    public IReadOnlyList<ProcessInfo> FindProcesses(Func<string, bool> nameFilter) => ProcessQuery.Find(nameFilter);

    public IReadOnlyList<DesktopWindowInfo> GetWindows() => WindowEnumerator.GetTopLevelWindows(_excludeProcessId);

    public string? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> GetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> GetFiles(string path, string pattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public string? ReadRegistryString(bool localMachine, string subKey, string valueName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(localMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                using var key = root.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string s && s.Length > 0)
                {
                    return s;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetRegistrySubKeys(bool localMachine, string subKey)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(localMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
                using var key = root.OpenSubKey(subKey);
                if (key is not null)
                {
                    names.UnionWith(key.GetSubKeyNames());
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return names.ToList();
    }

    public string ExpandEnvironment(string text) => Environment.ExpandEnvironmentVariables(text);
}
