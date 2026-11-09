using System.Text;
using ClickZen.Core;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Input;

namespace ClickZen.Device.Tests.Input;

public sealed class PersistentRootInjectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InputDeviceInfo SampleTouchscreen() =>
        GeteventDeviceParser.PickTouchscreen(
            GeteventDeviceParser.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "getevent", "synthetic_phone_getevent_p.txt"))))!;

    private sealed class FakeRootShellStream : Stream
    {
        private readonly Queue<byte> _rx = new();
        private readonly StringBuilder _txLog = new();
        private readonly bool _isRoot;

        public FakeRootShellStream(bool isRoot = true) => _isRoot = isRoot;

        public string WrittenCommands => _txLog.ToString();

        public bool FailWrites { get; set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = 0;
            while (n < buffer.Length && _rx.Count > 0)
            {
                buffer.Span[n++] = _rx.Dequeue();
            }

            return ValueTask.FromResult(n);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            OnWrite(Encoding.UTF8.GetString(buffer, offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites)
            {
                throw new IOException("broken adb root socket");
            }

            OnWrite(Encoding.UTF8.GetString(buffer.Span));
            return ValueTask.CompletedTask;
        }

        private void OnWrite(string text)
        {
            _txLog.Append(text);
            if (text.Contains("__CZ_ROOT_OK__", StringComparison.Ordinal))
            {
                var reply = _isRoot ? "__CZ_ROOT_OK__0\n" : "__CZ_ROOT_OK__2000\n";
                foreach (var b in Encoding.UTF8.GetBytes(reply))
                {
                    _rx.Enqueue(b);
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Reuses_single_persistent_root_shell_across_multiple_gestures_with_fd_redirection()
    {
        var openCalls = 0;
        var stream = new FakeRootShellStream(isRoot: true);
        var fallbackCalls = 0;
        var touch = SampleTouchscreen();

        var fallback = new RootSendeventInjector(
            (_, _) =>
            {
                fallbackCalls++;
                return Task.FromResult("");
            },
            touch,
            () => new SizeI(1080, 2400),
            () => DisplayRotation.Rotation0,
            rawEvents: true);

        await using var injector = new PersistentRootInjector(
            openRootStream: _ =>
            {
                openCalls++;
                return Task.FromResult<Stream>(stream);
            },
            fallback: fallback,
            touch: touch,
            rawEvents: true,
            displayId: () => 0,
            clock: new FakeClock());

        await injector.TapAsync(new PointI(540, 1200), 50, Ct);
        await injector.TapAsync(new PointI(200, 400), 50, Ct);
        await injector.StrokeAsync([new TimedPoint(100, 200, 0), new TimedPoint(100, 600, 32)], Ct);
        await injector.KeyAsync(4, Ct);

        // Only ONE persistent su socket was opened across all four operations, and fallback was never needed!
        Assert.Equal(1, openCalls);
        Assert.Equal(0, fallbackCalls);
        Assert.Contains("exec 4>/dev/input/event", stream.WrittenCommands, StringComparison.Ordinal);
        Assert.Contains(">&4", stream.WrittenCommands, StringComparison.Ordinal);
        Assert.Contains("input keyevent 4", stream.WrittenCommands, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reopens_persistent_shell_when_socket_drops_and_falls_back_cleanly_if_not_uid0()
    {
        var attempt = 0;
        var first = new FakeRootShellStream(isRoot: true);
        var second = new FakeRootShellStream(isRoot: false); // Fails uid=0 check immediately
        var fallbackCommands = new List<string>();
        var touch = SampleTouchscreen();

        var fallback = new RootSendeventInjector(
            (cmd, _) =>
            {
                fallbackCommands.Add(cmd);
                return Task.FromResult("");
            },
            touch,
            () => new SizeI(1080, 2400),
            () => DisplayRotation.Rotation0,
            rawEvents: true);

        await using var injector = new PersistentRootInjector(
            openRootStream: _ =>
            {
                attempt++;
                return Task.FromResult<Stream>(attempt == 1 ? first : second);
            },
            fallback: fallback,
            touch: touch,
            rawEvents: true,
            clock: new FakeClock());

        await injector.TapAsync(new PointI(100, 200), 40, Ct);
        Assert.Empty(fallbackCommands);

        // Simulate USB/Wi-Fi hiccup breaking the persistent socket; next tap falls back seamlessly and tries to reopen on subsequent tap.
        first.FailWrites = true;
        await injector.TapAsync(new PointI(300, 400), 40, Ct);
        await injector.TapAsync(new PointI(350, 450), 40, Ct);

        Assert.Equal(2, attempt);
        Assert.Equal(2, fallbackCommands.Count);
    }
}
