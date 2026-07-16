using System;
using System.Collections.Generic;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public sealed class SasayakiCueNavigationController
{
    private List<SasayakiMatch> _matches = [];

    public int CurrentCueIndex { get; private set; } = -1;
    public SasayakiMatch? CurrentMatch => GetMatchForCue(CurrentCueIndex);

    public IReadOnlyList<SasayakiMatch> Matches => _matches;

    public void Load(SasayakiMatchData data)
    {
        _matches = data.Matches;
        CurrentCueIndex = -1;
    }

    public void UpdatePosition(double seconds)
    {
        CurrentCueIndex = FindCueIndexAtPosition(seconds);
    }

    public int FindCueIndexAtPosition(double seconds)
    {
        if (_matches.Count == 0)
            return -1;

        var low = 0;
        var high = _matches.Count - 1;

        // Binary search: find the cue whose interval contains `seconds`
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var cue = _matches[mid];

            if (seconds >= cue.StartTime && seconds < cue.EndTime)
                return mid;

            if (seconds < cue.StartTime)
                high = mid - 1;
            else
                low = mid + 1;
        }

        // Between cues — return the nearest previous cue
        return Math.Max(0, high);
    }

    public int? GetNextCueIndex()
    {
        if (CurrentCueIndex < 0 || CurrentCueIndex >= _matches.Count - 1)
            return null;
        return CurrentCueIndex + 1;
    }

    public int? GetPreviousCueIndex()
    {
        if (CurrentCueIndex <= 0)
            return null;
        return CurrentCueIndex - 1;
    }

    public SasayakiMatch? GetMatchForCue(int cueIndex)
    {
        return cueIndex >= 0 && cueIndex < _matches.Count
            ? _matches[cueIndex]
            : null;
    }

    public int GetCueIndex(SasayakiMatch match)
    {
        var index = _matches.IndexOf(match);
        if (index >= 0)
            return index;

        return _matches.FindIndex(candidate =>
            string.Equals(candidate.Id, match.Id, StringComparison.Ordinal)
            && candidate.StartTime.Equals(match.StartTime));
    }

    public int? GetMatchedCueIndexBefore(double seconds)
    {
        for (var i = _matches.Count - 1; i >= 0; i--)
        {
            if (_matches[i].EndTime <= seconds)
                return i;
        }

        return null;
    }

    public int? GetMatchedCueIndexAfter(double seconds)
    {
        for (var i = 0; i < _matches.Count; i++)
        {
            if (_matches[i].StartTime >= seconds)
                return i;
        }

        return null;
    }

    public void SeekToCue(int cueIndex)
    {
        if (cueIndex >= 0 && cueIndex < _matches.Count)
            CurrentCueIndex = cueIndex;
    }
}
