using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellyflix.Configuration;

/// <summary>
/// Everything in here is user-editable via the admin config page.
/// Ship sensible defaults; let power users tune.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ────────────────────────────────────────────────────────────
    //  Anti-pattern detection knobs
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum number of items a user must have watched during a seasonal
    /// window (across all past years in their history) before we trust
    /// ourselves to detect a pattern. Below this, we fall back to a gentle
    /// seasonal nudge rather than assuming either pro- or counter-seasonal.
    /// </summary>
    public int MinHistoryThreshold { get; set; } = 3;

    /// <summary>
    /// Tag-overlap score above which we treat the user as firmly pro-seasonal
    /// and lean hard into the expected content. Range 0.0–1.0.
    /// </summary>
    public double AffinityThreshold { get; set; } = 0.6;

    /// <summary>
    /// Tag-overlap score below which we treat the user as firmly counter-seasonal
    /// and pivot the "Right Now" rail to their actual period preferences. Range 0.0–1.0.
    /// </summary>
    public double AversionThreshold { get; set; } = 0.15;

    // ────────────────────────────────────────────────────────────
    //  Seasonal windows — the default set
    // ────────────────────────────────────────────────────────────

    public List<SeasonalWindow> SeasonalWindows { get; set; } = new()
    {
        new SeasonalWindow
        {
            Key = "valentines",
            DisplayName = "Valentine's",
            StartMonth = 2, StartDay = 10,
            EndMonth = 2, EndDay = 16,
            ExpectedTags = new() { "Romance", "Romantic Comedy", "Love" }
        },
        new SeasonalWindow
        {
            Key = "halloween",
            DisplayName = "Halloween",
            StartMonth = 10, StartDay = 20,
            EndMonth = 11, EndDay = 1,
            ExpectedTags = new() { "Horror", "Thriller", "Supernatural", "Slasher" }
        },
        new SeasonalWindow
        {
            Key = "christmas",
            DisplayName = "Christmas",
            StartMonth = 12, StartDay = 15,
            EndMonth = 12, EndDay = 26,
            ExpectedTags = new() { "Christmas", "Holiday", "Family" }
        },
        new SeasonalWindow
        {
            Key = "newyears",
            DisplayName = "New Year's",
            StartMonth = 12, StartDay = 29,
            EndMonth = 1, EndDay = 2,
            ExpectedTags = new() { "Comedy", "Party", "Feel-good" }
        },
        new SeasonalWindow
        {
            Key = "summer",
            DisplayName = "Summer Blockbuster",
            StartMonth = 6, StartDay = 20,
            EndMonth = 8, EndDay = 31,
            ExpectedTags = new() { "Action", "Adventure", "Blockbuster" }
        },
        new SeasonalWindow
        {
            Key = "pride",
            DisplayName = "Pride",
            StartMonth = 6, StartDay = 1,
            EndMonth = 6, EndDay = 30,
            ExpectedTags = new() { "LGBTQ+", "LGBT" }
        }
    };

    // ────────────────────────────────────────────────────────────
    //  UX preferences
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// If true, hover previews play video with audio. Default off — silent only.
    /// </summary>
    public bool AutoplayWithSoundOnHover { get; set; } = false;

    /// <summary>
    /// Days after which an unfinished item is treated as "abandoned" rather
    /// than "in progress" and hidden from the Continue Watching rail.
    /// </summary>
    public int AbandonedThresholdDays { get; set; } = 30;
}

public class SeasonalWindow
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int StartMonth { get; set; }
    public int StartDay { get; set; }
    public int EndMonth { get; set; }
    public int EndDay { get; set; }
    public List<string> ExpectedTags { get; set; } = new();
}
