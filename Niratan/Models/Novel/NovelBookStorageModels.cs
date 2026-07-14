using System.Collections.Generic;
using Niratan.Models;

namespace Niratan.Models.Novel;

public sealed record NovelBookCatalogSnapshot(
    IReadOnlyList<NovelBook> Books,
    IReadOnlyList<string> CorruptMetadataPaths);
