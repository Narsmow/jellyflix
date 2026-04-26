using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jellyflix.Configuration;

namespace Jellyfin.Plugin.Jellyflix.Services;

/// <summary>
/// Given "today" and a config, tells us which seasonal window we're in
/// and whether an arbitrary historical date falls inside the same window
/// (across any prior year).
/// </summary>
public class SeasonalContext
{
    public SeasonalWindow? Current { get; }

    private readonly SeasonalWindow? _window;

    public SeasonalContext(SeasonalWindow? current)
    {
        Current = current;
        _window = current;
    }

    public static SeasonalContext Detect(DateTime now, IEnumerable<SeasonalWindow> windows)
    {
        foreach (var w in windows)
        {
            if (ContainsDate(w, now))
            {
                return new SeasonalContext(w);
            }
        }
        return new SeasonalContext(null);
    }

    /// <summary>
    /// Does this historical watch date fall inside the current seasonal window
    /// (in any year)? Handles windows that wrap across year boundaries
    /// (e.g. Dec 29–Jan 2).
    /// </summary>
    public bool MatchesHistorically(DateTime historicalDate)
    {
        if (_window is null) return false;
        return ContainsDate(_window, historicalDate);
    }

    private static bool ContainsDate(SeasonalWindow w, DateTime date)
    {
        // Build the current year's window, accounting for wrap-around.
        // A wrap window (e.g. Dec 29 → Jan 2) is checked as two half-windows.
        int m = date.Month;
        int d = date.Day;

        // Non-wrapping case: start <= end in the same calendar year
        bool wraps = (w.EndMonth < w.StartMonth)
                  || (w.EndMonth == w.StartMonth && w.EndDay < w.StartDay);

        if (!wraps)
        {
            return IsOnOrAfter(m, d, w.StartMonth, w.StartDay)
                && IsOnOrBefore(m, d, w.EndMonth, w.EndDay);
        }

        // Wraps: either (start .. Dec 31) or (Jan 1 .. end)
        return IsOnOrAfter(m, d, w.StartMonth, w.StartDay)
            || IsOnOrBefore(m, d, w.EndMonth, w.EndDay);
    }

    private static bool IsOnOrAfter(int m, int d, int startM, int startD)
        => (m > startM) || (m == startM && d >= startD);

    private static bool IsOnOrBefore(int m, int d, int endM, int endD)
        => (m < endM) || (m == endM && d <= endD);
}
