using FluentAssertions;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public sealed class AnkiDirectMediaStoreTests : IDisposable
{
    private readonly string _mediaDirectory = Path.Combine(
        Path.GetTempPath(),
        "Niratan.Tests",
        nameof(AnkiDirectMediaStoreTests),
        Guid.NewGuid().ToString("N"));

    public AnkiDirectMediaStoreTests()
    {
        Directory.CreateDirectory(_mediaDirectory);
    }

    [Fact]
    public async Task GenerateAsync_ReusesExistingNonEmptyDestination()
    {
        const string filename = "existing.m4a";
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(Path.Combine(_mediaDirectory, filename), [1, 2, 3], ct);
        var producerCalls = 0;

        var result = await AnkiDirectMediaStore.GenerateAsync(
            _mediaDirectory,
            filename,
            (_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromResult<string?>(null);
            },
            ct);

        result.Should().Be(filename);
        producerCalls.Should().Be(0);
    }

    [Fact]
    public async Task GenerateAsync_CoalescesSameDestinationAndPublishesOnlyAfterCompletion()
    {
        const string filename = "shared.webp";
        var ct = TestContext.Current.CancellationToken;
        var destination = Path.Combine(_mediaDirectory, filename);
        var producerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerCalls = 0;
        string? observedTempPath = null;

        Task<string?> Produce(string tempPath, CancellationToken ct)
        {
            return ProduceCoreAsync(tempPath, ct);
        }

        async Task<string?> ProduceCoreAsync(string tempPath, CancellationToken ct)
        {
            Interlocked.Increment(ref producerCalls);
            observedTempPath = tempPath;
            await File.WriteAllBytesAsync(tempPath, [4, 5, 6], ct);
            producerStarted.TrySetResult(true);
            await releaseProducer.Task.WaitAsync(ct);
            return tempPath;
        }

        var first = AnkiDirectMediaStore.GenerateAsync(
            _mediaDirectory,
            filename,
            Produce,
            ct);
        await producerStarted.Task.WaitAsync(ct);
        var second = AnkiDirectMediaStore.GenerateAsync(
            _mediaDirectory,
            filename,
            (_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromResult<string?>(null);
            },
            ct);

        File.Exists(destination).Should().BeFalse();
        observedTempPath.Should().NotBeNull();
        Path.GetDirectoryName(observedTempPath).Should().Be(_mediaDirectory);
        Path.GetExtension(observedTempPath).Should().Be(".webp");

        releaseProducer.TrySetResult(true);
        var results = await Task.WhenAll(first, second).WaitAsync(ct);

        results.Should().OnlyContain(value => value == filename);
        producerCalls.Should().Be(1);
        (await File.ReadAllBytesAsync(destination, ct)).Should().BeEquivalentTo([4, 5, 6]);
        Directory.GetFiles(_mediaDirectory).Should().Equal(destination);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotPublishOrReturnFilenameForEmptyOutput()
    {
        const string filename = "empty.m4a";
        var ct = TestContext.Current.CancellationToken;

        var result = await AnkiDirectMediaStore.GenerateAsync(
            _mediaDirectory,
            filename,
            async (tempPath, ct) =>
            {
                await File.WriteAllBytesAsync(tempPath, [], ct);
                return tempPath;
            },
            ct);

        result.Should().BeNull();
        File.Exists(Path.Combine(_mediaDirectory, filename)).Should().BeFalse();
        Directory.GetFiles(_mediaDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteBytesAsync_SanitizesFilenameAndRejectsEmptyData()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = await AnkiDirectMediaStore.WriteBytesAsync(
            _mediaDirectory,
            "../unsafe 音声.m4a",
            [7, 8, 9],
            ct);
        var empty = await AnkiDirectMediaStore.WriteBytesAsync(
            _mediaDirectory,
            "empty.bin",
            [],
            ct);
        var leadingDot = await AnkiDirectMediaStore.WriteBytesAsync(
            _mediaDirectory,
            "../../.hidden.webp",
            [10],
            ct);
        var reservedName = await AnkiDirectMediaStore.WriteBytesAsync(
            _mediaDirectory,
            "CON.m4a",
            [11],
            ct);

        stored.Should().Be("unsafe___.m4a");
        (await File.ReadAllBytesAsync(Path.Combine(_mediaDirectory, stored!), ct))
            .Should().BeEquivalentTo([7, 8, 9]);
        empty.Should().BeNull();
        File.Exists(Path.Combine(_mediaDirectory, "empty.bin")).Should().BeFalse();
        leadingDot.Should().Be("hidden.webp");
        File.Exists(Path.Combine(_mediaDirectory, "hidden.webp")).Should().BeTrue();
        reservedName.Should().Be("anki_CON.m4a");
        File.Exists(Path.Combine(_mediaDirectory, "anki_CON.m4a")).Should().BeTrue();
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_mediaDirectory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
