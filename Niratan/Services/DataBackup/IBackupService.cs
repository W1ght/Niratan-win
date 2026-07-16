using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Backup;

public enum HoshiBackupTarget
{
    Books,
    Dictionaries,
}

public sealed record TtuBackupImportResult(int AddedBooks, int UpdatedBooks);

public interface IBackupService
{
    Task CreateHoshiBackupAsync(
        HoshiBackupTarget target,
        string destinationPath,
        CancellationToken ct = default);

    Task RestoreHoshiBackupAsync(
        HoshiBackupTarget target,
        string archivePath,
        CancellationToken ct = default);

    Task ExportTtuBackupAsync(
        string destinationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task<TtuBackupImportResult> ImportTtuBackupAsync(
        string archivePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
