using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Backup;

public enum NiratanBackupTarget
{
    Books,
    Dictionaries,
}

public sealed record TtuBackupImportResult(int AddedBooks, int UpdatedBooks);

public interface IBackupService
{
    Task CreateNiratanBackupAsync(
        NiratanBackupTarget target,
        string destinationPath,
        CancellationToken ct = default);

    Task RestoreNiratanBackupAsync(
        NiratanBackupTarget target,
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
