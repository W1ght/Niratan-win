using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.ZLibrary;

namespace Niratan.Services.ZLibrary;

public interface IZLibraryCredentialStore
{
    bool HasCredentials { get; }

    Task<ZLibraryCredentials?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(ZLibraryCredentials credentials, CancellationToken ct = default);

    Task DeleteAsync(CancellationToken ct = default);
}
