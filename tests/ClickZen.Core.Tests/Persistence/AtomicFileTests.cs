using ClickZen.Core.Persistence;

namespace ClickZen.Core.Tests.Persistence;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cz-tests-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Creates_new_file_and_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteAllText(path, "hello");

        Assert.Equal("hello", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Replaces_existing_file()
    {
        var path = Path.Combine(_dir, "a.json");
        File.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Creates_missing_directories()
    {
        var path = Path.Combine(_dir, "x", "y", "z.txt");
        AtomicFile.WriteAllText(path, "deep");
        Assert.Equal("deep", File.ReadAllText(path));
    }

    [Fact]
    public async Task Async_write_round_trips_bytes()
    {
        var path = Path.Combine(_dir, "b.bin");
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        await AtomicFile.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        Assert.Equal(data, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }
}
