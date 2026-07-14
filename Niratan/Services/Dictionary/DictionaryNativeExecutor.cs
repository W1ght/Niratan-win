using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Dictionary;

internal static class DictionaryNativeExecutor
{
    public static async Task<T> RunAsync<T>(SemaphoreSlim gate, Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(operation);

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
