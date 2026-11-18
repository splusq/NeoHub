using System.Text;
using NeoHub.Services;

namespace NeoHub.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neohub-atomic-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ReplacesContent_AndLeavesNoTempFiles()
    {
        var path = Path.Combine(_dir, "userSettings.json");
        await File.WriteAllTextAsync(path, "old");

        await AtomicFile.WriteAllTextAsync(path, "new");

        Assert.Equal("new", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void WritesUtf8WithoutBom_LikeFileWriteAllText()
    {
        var path = Path.Combine(_dir, "settings.json");
        AtomicFile.WriteAllText(path, "{\"a\":\"é\"}");

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("{\"a\":\"é\"}", Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// A reader (like the reloadOnChange file watcher) must only ever see a complete old or new
    /// file. File.WriteAllText truncates first, so readers could see an empty or partial file.
    /// </summary>
    [Fact]
    public async Task ConcurrentReader_NeverSeesAPartialFile()
    {
        var path = Path.Combine(_dir, "userSettings.json");
        var contentA = new string('a', 200_000);
        var contentB = new string('b', 200_000);
        await File.WriteAllTextAsync(path, contentA);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var writer = Task.Run(async () =>
        {
            var flip = false;
            while (!stop.IsCancellationRequested)
                await AtomicFile.WriteAllTextAsync(path, (flip = !flip) ? contentB : contentA);
        });

        var torn = 0;
        while (!stop.IsCancellationRequested)
        {
            string text;
            try { text = await File.ReadAllTextAsync(path); }
            catch (IOException) { continue; }
            if (text != contentA && text != contentB)
                torn++;
        }
        await writer;

        Assert.Equal(0, torn);
    }
}
