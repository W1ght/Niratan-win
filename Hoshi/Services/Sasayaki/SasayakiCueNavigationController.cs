using System;
using System.Collections.Generic;
using Hoshi.Models.Sasayaki;

namespace Hoshi.Services.Sasayaki;

public sealed class SasayakiCueNavigationController
{
    private List<SasayakiCue> _cues = [];
    private List<SasayakiMatch> _matches = [];

    public int CurrentCueIndex { get; private set; } = -1;
    public SasayakiCue? CurrentCue => CurrentCueIndex >= 0 && CurrentCueIndex < _cues.Count
        ? _cues[CurrentCueIndex] : null;
    public SasayakiMatch? CurrentMatch => GetMatchForCue(CurrentCueIndex);

    public IReadOnlyList<SasayakiCue> Cues => _cues;
    public IReadOnlyList<SasayakiMatch> Matches => _matches;

    public void Load(SasayakiMatchData data)
    {
        _cues = data.Cues;
        _matches = data.Matches;
        CurrentCueIndex = -1;
    }

    public void UpdatePosition(double seconds)
    {
        CurrentCueIndex = FindCueIndexAtPosition(seconds);
    }

    public int FindCueIndexAtPosition(double seconds)
    {
        if (_cues.Count == 0)
            return -1;

        var low = 0;
        var high = _cues.Count - 1;

        // Binary search: find the cue whose interval contains `seconds`
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var cue = _cues[mid];

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
        if (CurrentCueIndex < 0 || CurrentCueIndex >= _cues.Count - 1)
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
        foreach (var match in _matches)
        {
            if (match.CueIndex == cueIndex)
                return match;
        }

        return null;
    }

    public int? GetMatchedCueIndexBefore(double seconds)
    {
        for (var i = _matches.Count - 1; i >= 0; i--)
        {
            var cueIndex = _matches[i].CueIndex;
            if (cueIndex < _cues.Count && _cues[cueIndex].EndTime <= seconds)
                return cueIndex;
        }

        return null;
    }

    public int? GetMatchedCueIndexAfter(double seconds)
    {
        foreach (var match in _matches)
        {
            if (match.CueIndex < _cues.Count && _cues[match.CueIndex].StartTime >= seconds)
                return match.CueIndex;
        }

        return null;
    }

    public void SeekToCue(int cueIndex)
    {
        if (cueIndex >= 0 && cueIndex < _cues.Count)
            CurrentCueIndex = cueIndex;
    }
}
