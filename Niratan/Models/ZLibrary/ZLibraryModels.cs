using System;
using System.Collections.Generic;

namespace Niratan.Models.ZLibrary;

public sealed record ZLibraryCredentials(
    string BaseUrl,
    string Email,
    string Password);

public sealed record ZLibrarySession(
    Uri BaseUri,
    string UserId,
    string UserKey);

public sealed record ZLibraryBook(
    string Id,
    string Hash,
    string Title,
    string Author,
    string Extension,
    string Size,
    long? FileSize,
    string Language,
    int? Year,
    Uri? CoverUri,
    string? DirectDownloadPath = null,
    string? DetailPath = null);

public sealed record ZLibrarySearchOptions(
    string Query,
    bool ExactMatching = false,
    int? YearFrom = null,
    int? YearTo = null,
    string? Language = null,
    string? Extension = "EPUB");

public sealed record ZLibrarySearchResult(
    IReadOnlyList<ZLibraryBook> Books,
    int? TotalCount,
    string? TotalCountLabel,
    int Page,
    int? TotalPages);
