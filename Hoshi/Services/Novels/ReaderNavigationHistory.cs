using System;
using System.Collections.Generic;

namespace Hoshi.Services.Novels;

public readonly record struct ReaderNavigationPosition(
    int ChapterIndex,
    double Progress)
{
    public ReaderNavigationPosition Clamp() =>
        new(ChapterIndex, Math.Clamp(Progress, 0, 1));
}

public sealed class ReaderNavigationHistory
{
    private readonly List<ReaderNavigationPosition> _back = [];
    private readonly List<ReaderNavigationPosition> _forward = [];

    public ReaderNavigationPosition? BackTarget =>
        _back.Count == 0 ? null : _back[^1];

    public ReaderNavigationPosition? ForwardTarget =>
        _forward.Count == 0 ? null : _forward[^1];

    public void Record(ReaderNavigationPosition position)
    {
        position = position.Clamp();
        if (_back.Count == 0 || _back[^1] != position)
            _back.Add(position);
        _forward.Clear();
    }

    public bool TryGoBack(
        ReaderNavigationPosition current,
        out ReaderNavigationPosition target)
    {
        if (_back.Count == 0)
        {
            target = default;
            return false;
        }

        target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        PushDistinct(_forward, current.Clamp());
        return true;
    }

    public bool TryGoForward(
        ReaderNavigationPosition current,
        out ReaderNavigationPosition target)
    {
        if (_forward.Count == 0)
        {
            target = default;
            return false;
        }

        target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        PushDistinct(_back, current.Clamp());
        return true;
    }

    public void ClearForward() => _forward.Clear();

    private static void PushDistinct(
        List<ReaderNavigationPosition> stack,
        ReaderNavigationPosition position)
    {
        if (stack.Count == 0 || stack[^1] != position)
            stack.Add(position);
    }
}
