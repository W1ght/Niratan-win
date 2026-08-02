using FluentAssertions;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class VideoSameFolderPlaylistResolverTests
{
    [Fact]
    public async Task Resolve_UsesNaturalOrderAndDoesNotModifyMedia()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var episode10 = Path.Combine(temp.Path, "Episode 10.mkv");
        var episode2 = Path.Combine(temp.Path, "Episode 2.mkv");
        var episode1 = Path.Combine(temp.Path, "Episode 1.mkv");
        await File.WriteAllBytesAsync(episode10, [10], ct);
        await File.WriteAllBytesAsync(episode2, [2], ct);
        await File.WriteAllBytesAsync(episode1, [1], ct);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "notes.txt"), "ignored", ct);
        var timestamps = new[] { episode1, episode2, episode10 }
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);

        var items = new VideoSameFolderPlaylistResolver().Resolve(episode2);

        items.Select(item => Path.GetFileName(item.FilePath)).Should().Equal(
            "Episode 1.mkv", "Episode 2.mkv", "Episode 10.mkv");
        foreach (var pair in timestamps)
            File.GetLastWriteTimeUtc(pair.Key).Should().Be(pair.Value);
    }
}
