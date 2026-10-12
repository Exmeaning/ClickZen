using ClickZen.Core.Emulators;
using ClickZen.Platform.Emulators;

namespace ClickZen.Platform.Tests;

/// <summary>Checks against the real system (excluded from CI by the Device category).</summary>
[Trait("Category", "Device")]
public sealed class EmulatorDetectorDeviceTests
{
    [Fact]
    public void Reads_its_own_command_line_and_path()
    {
        var pid = (uint)Environment.ProcessId;
        var commandLine = ProcessQuery.GetCommandLine(pid);
        Assert.False(string.IsNullOrEmpty(commandLine));
        Assert.Contains(Path.GetFileNameWithoutExtension(Environment.ProcessPath!), commandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Environment.ProcessPath, ProcessQuery.GetImagePath(pid), StringComparer.OrdinalIgnoreCase);
        Assert.Null(ProcessQuery.GetCommandLine(0));
    }

    /// <summary>Needs a running Android Studio emulator (any AVD); skipped otherwise.</summary>
    [Fact]
    public void Detects_a_running_avd()
    {
        var qemu = ProcessQuery.Find(n => n.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(qemu.Count == 0, "No Android Studio emulator is running.");

        var found = new EmulatorDetector(new SystemEmulatorEnvironment((uint)Environment.ProcessId)).Detect();
        var avd = Assert.Single(found.Running, r => r.Kind == EmulatorKind.AndroidStudio && r.ProcessId == qemu[0].Id);
        Assert.Equal(AdbPortSource.Runtime, avd.PortSource);
        Assert.InRange(avd.AdbPort ?? 0, 5555, 5683);
        Assert.False(string.IsNullOrEmpty(avd.InstanceKey));
        Assert.True(avd.HasWindow, "the AVD window was not found");
        Assert.Contains(avd.InstanceKey, avd.WindowTitle, StringComparison.Ordinal);
    }
}
