using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;

namespace ClickZen.Core.Tests.Fakes;

/// <summary>Frame source whose image is a solid colour; each SetColor bumps the sequence.</summary>
public sealed class FakeFrameSource : IFrameSource
{
    private Frame? _latest;
    private long _seq;

    public FakeFrameSource(int width = 100, int height = 200) => Size = new SizeI(width, height);

    public SizeI Size { get; }

    public int WaitCalls { get; private set; }

    /// <summary>Called whenever the engine asks for a frame newer than its last action – simulates the screen reacting.</summary>
    public Action<FakeFrameSource>? OnWaitForNewer { get; set; }

    public Frame? Latest => _latest;

    public void SetColor(byte r, byte g, byte b)
    {
        var px = new byte[Size.Width * Size.Height * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }

        _latest = new Frame(Size.Width, Size.Height, px, ++_seq);
    }

    public Task<Frame?> WaitForFrameAsync(long afterSequence, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        WaitCalls++;
        if (afterSequence >= 0 && (_latest is null || _latest.Sequence <= afterSequence))
        {
            OnWaitForNewer?.Invoke(this);
        }

        return Task.FromResult(_latest);
    }
}

/// <summary>Matcher that "finds" templates listed in <see cref="Visible"/> at a fixed frame location.</summary>
public sealed class FakeMatcher : IImageMatcher
{
    public Dictionary<string, RectI> Visible { get; } = new(StringComparer.Ordinal);

    public List<(string TemplateId, RectI Area, double Scale)> Calls { get; } = [];

    public MatchResult Match(Frame frame, RectI searchArea, TemplateAsset template, double templateScale, ImageCondition options)
    {
        Calls.Add((template.Id, searchArea, templateScale));
        if (Visible.TryGetValue(template.Id, out var where) && searchArea.ClampTo(new RectI(where.X, where.Y, Math.Max(1, where.Width), Math.Max(1, where.Height))) is { IsEmpty: false })
        {
            return new MatchResult(true, 0.97, where);
        }

        return MatchResult.NotFound(0.2);
    }

    public void ClearCache()
    {
    }
}
