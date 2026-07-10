using System;
using System.Threading.Tasks;

namespace Hoshi.Services.Dictionary;

internal sealed class DictionaryPopupWarmCoordinator
{
    private readonly object _gate = new();
    private Task? _warmTask;
    private bool _isWarm;
    private int _version;

    public bool IsWarm
    {
        get
        {
            lock (_gate)
                return _isWarm;
        }
    }

    public Task EnsureWarmAsync(Func<Task> warmAsync)
    {
        ArgumentNullException.ThrowIfNull(warmAsync);

        lock (_gate)
        {
            if (_isWarm)
                return Task.CompletedTask;
            if (_warmTask is not null)
                return _warmTask;

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var task = completion.Task;
            var version = _version;
            _warmTask = task;
            _ = RunWarmAsync(warmAsync, completion, task, version);
            return task;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _version++;
            _isWarm = false;
            _warmTask = null;
        }
    }

    private async Task RunWarmAsync(
        Func<Task> warmAsync,
        TaskCompletionSource completion,
        Task task,
        int version)
    {
        try
        {
            await warmAsync();
            lock (_gate)
            {
                if (version == _version)
                    _isWarm = true;
                if (ReferenceEquals(_warmTask, task))
                    _warmTask = null;
            }
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_warmTask, task))
                    _warmTask = null;
            }
            completion.TrySetException(ex);
        }
    }
}
