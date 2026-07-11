using System;

namespace Hoshi.Services.Dictionary;

internal sealed class DictionaryPopupLatestRequestQueue<T> where T : class
{
    private readonly object _gate = new();
    private T? _latest;

    public T? Replace(T request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var displaced = _latest;
            _latest = request;
            return displaced;
        }
    }

    public bool TryTake(out T? request)
    {
        lock (_gate)
        {
            request = _latest;
            _latest = null;
        }

        return request is not null;
    }

    public T? Clear()
    {
        lock (_gate)
        {
            var cleared = _latest;
            _latest = null;
            return cleared;
        }
    }
}
