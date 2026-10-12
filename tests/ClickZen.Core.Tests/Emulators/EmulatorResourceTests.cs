using System.Xml.Linq;
using ClickZen.Core.Emulators;

namespace ClickZen.Core.Tests.Emulators;

/// <summary>
/// The Devices page composes emulator resource keys from enum values ("Emu_Kind_" + kind, "Emu_State_" + state…);
/// the generic resource test only sees the prefixes, so every composed key is checked here.
/// </summary>
public sealed class EmulatorResourceTests
{
    private static HashSet<string>? Keys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClickZen.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return null;
        }

        var resw = Path.Combine(dir.FullName, "src", "ClickZen.App", "Strings", "en-US", "Resources.resw");
        return XDocument.Load(resw).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_composed_emulator_key_exists()
    {
        var keys = Keys();
        Assert.SkipWhen(keys is null, "Source tree not found.");

        // EmulatorConnectionState lives in the app; its names are listed here and must stay in sync.
        string[] states = ["NotConnected", "Connected", "Offline", "AdbDisabled", "PortUnknown"];
        var expected = Enum.GetNames<EmulatorKind>().Select(n => "Emu_Kind_" + n)
            .Concat(Enum.GetNames<AdbPortSource>().Select(n => "Emu_Source_" + n))
            .Concat(Enum.GetNames<ScreenAreaMethod>().Select(n => "Emu_Method_" + n))
            .Concat(states.Select(n => "Emu_State_" + n));

        var missing = expected.Where(k => !keys!.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Missing resources: " + string.Join(", ", missing));
    }
}
