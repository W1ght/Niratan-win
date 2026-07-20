using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;

namespace Niratan.Services.UI;

internal sealed class FileRevealService : IFileRevealService
{
    public Task<Result> RevealInFileExplorerAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)
            || (!File.Exists(filePath) && !Directory.Exists(filePath)))
        {
            return Task.FromResult(Result.Failure(
                "The video file no longer exists.",
                "File not found"));
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(filePath);
            var isDirectory = Directory.Exists(fullPath);
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = isDirectory ? $"\"{fullPath}\"" : $"/select,\"{fullPath}\"",
                UseShellExecute = true,
            });

            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Result.Cancelled());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure(
                ex.Message,
                "Could not reveal file"));
        }
    }
}
