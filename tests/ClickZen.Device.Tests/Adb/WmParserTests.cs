using ClickZen.Core.Geometry;
using ClickZen.Device.Adb.Parsing;

namespace ClickZen.Device.Tests.Adb;

public sealed class WmParserTests
{
    [Fact]
    public void Physical_only()
    {
        var r = WmParser.ParseSize(SampleFiles.Read("wm/size_physical.txt"));
        Assert.Equal(new SizeI(1080, 2400), r.Physical);
        Assert.False(r.HasOverride);
        Assert.Equal(new SizeI(1080, 2400), r.Effective);
    }

    [Fact]
    public void Override_wins_and_physical_is_kept_with_crlf()
    {
        var r = WmParser.ParseSize(SampleFiles.Read("wm/size_override_crlf.txt"));
        Assert.Equal(new SizeI(1440, 3200), r.Physical);
        Assert.Equal(new SizeI(1080, 2400), r.Override);
        Assert.True(r.HasOverride);
        Assert.Equal(new SizeI(1080, 2400), r.Effective);
    }

    [Fact]
    public void Combined_size_and_density_output()
    {
        var text = SampleFiles.Read("wm/size_density_combined.txt");
        var size = WmParser.ParseSize(text);
        var density = WmParser.ParseDensity(text);
        Assert.Equal(new SizeI(720, 1560), size.Effective);
        Assert.Equal(new SizeI(1080, 2340), size.Physical);
        Assert.Equal(320, density.Effective);
        Assert.Equal(440, density.Physical);
    }

    [Fact]
    public void Density_physical_only()
    {
        var d = WmParser.ParseDensity(SampleFiles.Read("wm/density_physical.txt"));
        Assert.Equal(480, d.Physical);
        Assert.False(d.HasOverride);
        Assert.Equal(480, d.Effective);
    }

    [Fact]
    public void Density_override_wins()
    {
        var d = WmParser.ParseDensity(SampleFiles.Read("wm/density_override.txt"));
        Assert.Equal(560, d.Physical);
        Assert.Equal(420, d.Override);
        Assert.Equal(420, d.Effective);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/system/bin/sh: wm: not found")]
    [InlineData("Error: Could not access the Window Manager. Is the system running?")]
    [InlineData("Physical size: 0x0")]
    public void Garbage_yields_empty(string? text)
    {
        Assert.True(WmParser.ParseSize(text).IsEmpty);
        Assert.Equal(0, WmParser.ParseDensity(text).Effective);
    }

    [Fact]
    public void Override_only_falls_back_to_override()
    {
        var r = WmParser.ParseSize("Override size: 900x1600");
        Assert.True(r.Physical.IsEmpty);
        Assert.Equal(new SizeI(900, 1600), r.Effective);
    }

    [Fact]
    public void Multi_display_takes_first_physical()
    {
        var r = WmParser.ParseSize("Physical size: 1080x2400\nPhysical size: 1920x1080\n");
        Assert.Equal(new SizeI(1080, 2400), r.Physical);
    }

    [Fact]
    public void Tolerates_extra_whitespace()
    {
        var r = WmParser.ParseSize("  Physical size:   1200 x 1920  ");
        Assert.Equal(new SizeI(1200, 1920), r.Physical);
    }
}
