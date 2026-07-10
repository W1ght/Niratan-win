using System.Collections.Generic;
using Hoshi.Models;

namespace Hoshi.Models.Novel;

public sealed record NovelBookCatalogSnapshot(
    IReadOnlyList<NovelBook> Books,
    IReadOnlyList<string> CorruptMetadataPaths);
