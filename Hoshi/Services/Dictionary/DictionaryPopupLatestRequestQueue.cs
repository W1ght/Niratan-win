using System;

namespace Hoshi.Services.Dictionary;

internal sealed class DictionaryPopupLatestRequestQueue<T> where T : class
{
    private readonly object _gate = new();
    private T? _latest;

    public void Replace(T request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
            _latest = request;
    }

    public bool TryTake(Func<T, bool> isEligible, out T? request)
    {
        ArgumentNullException.ThrowIfNull(isEligible);
        lock (_gate)
        {
            request = _latest;
            _latest = null;
        }

        if (request is null || !isEligible(request))
        {
            request = null;
            return false;
        }

        return true;
    }

    public void Clear()
    {
        lock (_gate)
            _latest = null;
    }
}
