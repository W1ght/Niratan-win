using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Dictionary;

internal readonly record struct DictionaryPopupWarmLease(
    long Version,
    CancellationToken CancellationToken,
    Func<long, bool> IsCurrent)
{
    public void ThrowIfInvalid()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(Version))
            throw new OperationCanceledException("Popup warm operation was superseded.", CancellationToken);
    }
}

internal sealed class DictionaryPopupWarmCoordinator
{
    private sealed class WarmOperation(long version)
    {
        public long Version { get; } = version;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private WarmOperation? _operation;
    private bool _isWarm;
    private long _version;

    public bool IsWarm
    {
        get
        {
            lock (_gate)
                return _isWarm;
        }
    }

    public Task EnsureWarmAsync(Func<DictionaryPopupWarmLease, Task> warmAsync)
    {
        ArgumentNullException.ThrowIfNull(warmAsync);
        WarmOperation operation;

        lock (_gate)
        {
            if (_isWarm)
                return Task.CompletedTask;
            if (_operation is not null)
                return _operation.Completion.Task;

            operation = new WarmOperation(++_version);
            _operation = operation;
        }

        _ = RunWarmAsync(warmAsync, operation);
        return operation.Completion.Task;
    }

    public void Reset()
    {
        WarmOperation? operation;
        lock (_gate)
        {
            _version++;
            _isWarm = false;
            operation = _operation;
            _operation = null;
        }

        if (operation is null)
            return;
        operation.Cancellation.Cancel();
        operation.Completion.TrySetCanceled(operation.Cancellation.Token);
    }

    private bool IsCurrent(long version)
    {
        lock (_gate)
            return _operation?.Version == version && _version == version;
    }

    private async Task RunWarmAsync(
        Func<DictionaryPopupWarmLease, Task> warmAsync,
        WarmOperation operation)
    {
        var lease = new DictionaryPopupWarmLease(
            operation.Version,
            operation.Cancellation.Token,
            IsCurrent);
        try
        {
            await warmAsync(lease);
            lease.ThrowIfInvalid();
            lock (_gate)
            {
                if (!ReferenceEquals(_operation, operation)
                    || _version != operation.Version)
                {
                    throw new OperationCanceledException(
                        "Popup warm operation was superseded.",
                        operation.Cancellation.Token);
                }

                _operation = null;
                _isWarm = true;
            }
            operation.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operation, operation))
                    _operation = null;
            }
            operation.Completion.TrySetException(ex);
        }
        finally
        {
            operation.Cancellation.Dispose();
        }
    }
}
