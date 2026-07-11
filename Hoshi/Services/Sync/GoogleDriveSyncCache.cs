using System;
using System.Collections.Generic;

namespace Hoshi.Services.Sync;

public interface IGoogleDriveSyncCache
{
    string? RootFolderId { get; set; }

    bool TryGetBookFolder(string bookTitle, out string folderId);

    void SetBookFolder(string bookTitle, string folderId);

    void RemoveBookFolder(string bookTitle);

    void Clear();
}

public sealed class GoogleDriveSyncCache : IGoogleDriveSyncCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _bookFolders = new(StringComparer.Ordinal);

    public string? RootFolderId { get; set; }

    public bool TryGetBookFolder(string bookTitle, out string folderId)
    {
        lock (_gate)
        {
            return _bookFolders.TryGetValue(bookTitle, out folderId!);
        }
    }

    public void SetBookFolder(string bookTitle, string folderId)
    {
        lock (_gate)
        {
            _bookFolders[bookTitle] = folderId;
        }
    }

    public void RemoveBookFolder(string bookTitle)
    {
        lock (_gate)
        {
            _bookFolders.Remove(bookTitle);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            RootFolderId = null;
            _bookFolders.Clear();
        }
    }
}
