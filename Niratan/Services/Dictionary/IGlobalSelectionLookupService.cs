using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Dictionary;

public interface IGlobalSelectionLookupService
{
    string StatusText { get; }

    event EventHandler? StatusChanged;

    Task InitializeAsync(CancellationToken ct = default);
    Task TriggerLookupAsync(CancellationToken ct = default);
}
