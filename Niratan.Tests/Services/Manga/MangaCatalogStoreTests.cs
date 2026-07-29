using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaCatalogStoreTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsCatalogWithoutSqlite()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Manga", "catalog.json");
        var store = new MangaCatalogStore(path);
        var catalog = new MangaLibraryCatalog
        {
            Books =
            [
                new MangaBook
                {
                    Id = "book",
                    Title = "漫画",
                    OriginalTitle = "漫画",
                    SourcePath = @"C:\Books\漫画.cbz",
                    CurrentPageIndex = 4,
                    Pages = Enumerable.Range(0, 10)
                        .Select(index => new MangaPageDescriptor
                        {
                            Index = index,
                            Path = $"{index:D3}.jpg",
                        })
                        .ToList(),
                },
            ],
            ReaderPreferences = new MangaReaderPreferences
            {
                Layout = MangaReaderLayout.DoublePage,
                Direction = MangaReadingDirection.RightToLeft,
                ZoomPercentage = 125,
            },
        };

        await store.SaveAsync(catalog, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        loaded.Books.Should().ContainSingle();
        loaded.Books[0].Title.Should().Be("漫画");
        loaded.Books[0].CurrentPageIndex.Should().Be(4);
        loaded.ReaderPreferences.Layout.Should().Be(MangaReaderLayout.DoublePage);
        File.ReadAllText(path).Should().Contain("\"books\"");
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_DoesNotSilentlyResetCatalog()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "catalog.json");
        await File.WriteAllTextAsync(path, "{broken");
        var store = new MangaCatalogStore(path);

        var action = () => store.LoadAsync(TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*invalid*");
    }
}
