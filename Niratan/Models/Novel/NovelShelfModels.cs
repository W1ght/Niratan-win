using System.Collections.Generic;

namespace Niratan.Models.Novel;

public sealed record NovelShelf(
    string Name,
    IReadOnlyList<string> BookIds);

public sealed record NovelShelfState(
    IReadOnlyList<NovelShelf> Shelves,
    IReadOnlyList<string> UnshelvedBookOrder);
