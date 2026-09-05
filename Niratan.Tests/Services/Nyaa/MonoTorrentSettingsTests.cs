using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Nyaa;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Nyaa;

public sealed class MonoTorrentSettingsTests
{
    [Fact]
    public void Normalize_preserves_supported_trackers_deduplicates_and_clamps_limits()
    {
        var settings = new MonoTorrentSettings
        {
            DownloadRootPath = Path.Combine(Path.GetTempPath(), "niratan-downloads"),
            AdditionalTrackers =
            [
                " udp://tracker.example:6969/announce ",
                "UDP://TRACKER.EXAMPLE:6969/announce",
                "https://tracker.example/announce",
                "file:///C:/tracker",
            ],
            ListenPort = 70_000,
            MaximumConnections = 100,
            MaximumConnectionsPerTorrent = 500,
            MaximumHalfOpenConnections = 0,
            MaximumOpenFiles = 5000,
            DownloadRateLimitKiB = -1,
            UploadRateLimitKiB = 2_000_000,
            UploadSlotsPerTorrent = 0,
        };

        var normalized = settings.Normalize();

        normalized.AdditionalTrackers.Should().Equal(
            "udp://tracker.example:6969/announce",
            "https://tracker.example/announce");
        normalized.DownloadRootPath.Should().Be(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(settings.DownloadRootPath)));
        normalized.ListenPort.Should().Be(65535);
        normalized.MaximumConnectionsPerTorrent.Should().Be(100);
        normalized.MaximumHalfOpenConnections.Should().Be(1);
        normalized.MaximumOpenFiles.Should().Be(1024);
        normalized.DownloadRateLimitKiB.Should().Be(0);
        normalized.UploadRateLimitKiB.Should().Be(1_000_000);
        normalized.UploadSlotsPerTorrent.Should().Be(1);
    }

    [Theory]
    [InlineData("downloads")]
    [InlineData("..\\downloads")]
    public void TryNormalizeDownloadRootPath_rejects_relative_paths(string value)
    {
        MonoTorrentSettings.TryNormalizeDownloadRootPath(value, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Download_root_policy_accepts_a_writable_directory_and_cleans_its_probe()
    {
        using var temporaryDirectory = new TempDirectory();

        var issue = await MonoTorrentDownloadRootPolicy.CheckWritableAsync(
            temporaryDirectory.Path,
            TestContext.Current.CancellationToken);

        issue.Should().BeNull();
        Directory.GetFiles(temporaryDirectory.Path, ".niratan-write-probe-*")
            .Should().BeEmpty();
    }

    [Fact]
    public void Download_root_policy_uses_the_configured_root_without_creating_it()
    {
        var configured = Path.Combine(Path.GetTempPath(), "niratan-configured-root");

        var resolved = MonoTorrentDownloadRootPolicy.Resolve(new MonoTorrentSettings
        {
            DownloadRootPath = configured,
        });

        resolved.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)));
    }

    [Theory]
    [InlineData("https://user:secret@tracker.example/announce")]
    [InlineData("https://tracker.example/announce#fragment")]
    [InlineData("file:///C:/tracker")]
    [InlineData("tracker.example/announce")]
    [InlineData("")]
    public void TryNormalizeTrackerUrl_rejects_unsafe_or_unsupported_values(string value)
    {
        MonoTorrentSettings.TryNormalizeTrackerUrl(value, out _).Should().BeFalse();
    }

    [Fact]
    public void MonoTorrent_builders_apply_engine_and_per_torrent_settings()
    {
        var settings = new MonoTorrentSettings
        {
            ListenPort = 51413,
            EnablePortForwarding = false,
            EnableDht = false,
            EnablePeerExchange = false,
            EnableLocalPeerDiscovery = false,
            MaximumConnections = 321,
            MaximumConnectionsPerTorrent = 123,
            MaximumHalfOpenConnections = 17,
            MaximumOpenFiles = 77,
            DownloadRateLimitKiB = 4096,
            UploadRateLimitKiB = 512,
            UploadSlotsPerTorrent = 5,
        };

        var engine = MonoTorrentDownloadService.CreateEngineSettings(
            settings,
            Path.GetTempPath());
        var torrent = MonoTorrentDownloadService.CreateTorrentSettings(settings);

        engine.AllowLocalPeerDiscovery.Should().BeFalse();
        engine.AllowPortForwarding.Should().BeFalse();
        engine.DhtEndPoint.Should().NotBeNull();
        engine.DhtEndPoint!.Port.Should().Be(51413);
        engine.ListenEndPoints["ipv4"].Port.Should().Be(51413);
        engine.ListenEndPoints["ipv6"].Port.Should().Be(51413);
        engine.MaximumConnections.Should().Be(321);
        engine.MaximumHalfOpenConnections.Should().Be(17);
        engine.MaximumOpenFiles.Should().Be(77);
        engine.MaximumDownloadRate.Should().Be(4096 * 1024);
        engine.MaximumUploadRate.Should().Be(512 * 1024);
        torrent.AllowDht.Should().BeFalse();
        torrent.AllowPeerExchange.Should().BeFalse();
        torrent.MaximumConnections.Should().Be(123);
        torrent.UploadSlots.Should().Be(5);
    }
}
