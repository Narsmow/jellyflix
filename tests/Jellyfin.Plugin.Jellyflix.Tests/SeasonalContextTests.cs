using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellyflix.Configuration;
using Jellyfin.Plugin.Jellyflix.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellyflix.Tests;

public class SeasonalContextTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static SeasonalWindow Valentine() => new()
    {
        Key = "valentines", DisplayName = "Valentine's",
        StartMonth = 2, StartDay = 10,
        EndMonth = 2, EndDay = 16,
        ExpectedTags = new() { "Romance" }
    };

    private static SeasonalWindow NewYears() => new()
    {
        Key = "newyears", DisplayName = "New Year's",
        StartMonth = 12, StartDay = 29,
        EndMonth = 1, EndDay = 2,
        ExpectedTags = new() { "Comedy", "Party" }
    };

    private static SeasonalWindow Summer() => new()
    {
        Key = "summer", DisplayName = "Summer",
        StartMonth = 6, StartDay = 20,
        EndMonth = 8, EndDay = 31,
        ExpectedTags = new() { "Action" }
    };

    // ── Detect: non-wrapping window ────────────────────────────────────────────

    [Fact]
    public void Detect_ReturnsWindow_WhenDateInsideNonWrappingWindow()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 13), new[] { Valentine() });

        Assert.NotNull(ctx.Current);
        Assert.Equal("valentines", ctx.Current!.Key);
    }

    [Fact]
    public void Detect_ReturnsNull_WhenDateBeforeWindow()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 9), new[] { Valentine() });

        Assert.Null(ctx.Current);
    }

    [Fact]
    public void Detect_ReturnsNull_WhenDateAfterWindow()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 17), new[] { Valentine() });

        Assert.Null(ctx.Current);
    }

    [Fact]
    public void Detect_IncludesStartBoundary()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 10), new[] { Valentine() });

        Assert.NotNull(ctx.Current);
    }

    [Fact]
    public void Detect_IncludesEndBoundary()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 16), new[] { Valentine() });

        Assert.NotNull(ctx.Current);
    }

    // ── Detect: year-wrapping window (New Year's: Dec 29 → Jan 2) ─────────────

    [Fact]
    public void Detect_WrappingWindow_MatchesDecemberPart()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2024, 12, 31), new[] { NewYears() });

        Assert.NotNull(ctx.Current);
        Assert.Equal("newyears", ctx.Current!.Key);
    }

    [Fact]
    public void Detect_WrappingWindow_MatchesJanuaryPart()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 1, 1), new[] { NewYears() });

        Assert.NotNull(ctx.Current);
        Assert.Equal("newyears", ctx.Current!.Key);
    }

    [Fact]
    public void Detect_WrappingWindow_MatchesJanuaryEndBoundary()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 1, 2), new[] { NewYears() });

        Assert.NotNull(ctx.Current);
    }

    [Fact]
    public void Detect_WrappingWindow_ExcludesDateOutsideWindow()
    {
        // March is well outside Dec 29–Jan 2.
        var ctx = SeasonalContext.Detect(new DateTime(2025, 3, 15), new[] { NewYears() });

        Assert.Null(ctx.Current);
    }

    [Fact]
    public void Detect_WrappingWindow_IncludesDecemberStartBoundary()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2024, 12, 29), new[] { NewYears() });

        Assert.NotNull(ctx.Current);
    }

    // ── Detect: multiple windows ───────────────────────────────────────────────

    [Fact]
    public void Detect_SelectsCorrectWindowFromMultiple()
    {
        var windows = new[] { Valentine(), NewYears(), Summer() };

        var ctx = SeasonalContext.Detect(new DateTime(2025, 7, 4), windows);

        Assert.NotNull(ctx.Current);
        Assert.Equal("summer", ctx.Current!.Key);
    }

    [Fact]
    public void Detect_ReturnsNull_WhenNoWindowMatches()
    {
        // April is outside all three windows.
        var windows = new[] { Valentine(), NewYears(), Summer() };

        var ctx = SeasonalContext.Detect(new DateTime(2025, 4, 15), windows);

        Assert.Null(ctx.Current);
    }

    [Fact]
    public void Detect_EmptyWindowList_ReturnsNull()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 6, 1), new List<SeasonalWindow>());

        Assert.Null(ctx.Current);
    }

    // ── MatchesHistorically ────────────────────────────────────────────────────

    [Fact]
    public void MatchesHistorically_ReturnsFalse_WhenNoCurrentWindow()
    {
        var ctx = new SeasonalContext(null);

        Assert.False(ctx.MatchesHistorically(new DateTime(2024, 2, 13)));
    }

    [Fact]
    public void MatchesHistorically_ReturnsTrue_ForSamePeriodInPriorYear()
    {
        // "Today" is Valentine's 2025; historical date in Feb 2023 should match.
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 13), new[] { Valentine() });

        Assert.True(ctx.MatchesHistorically(new DateTime(2023, 2, 14)));
    }

    [Fact]
    public void MatchesHistorically_ReturnsFalse_ForDifferentMonth()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 2, 13), new[] { Valentine() });

        Assert.False(ctx.MatchesHistorically(new DateTime(2023, 7, 14)));
    }

    [Fact]
    public void MatchesHistorically_WrappingWindow_MatchesDecemberInPriorYear()
    {
        // "Today" is Jan 1 2025 (inside New Year's window); Dec 30 2022 should match.
        var ctx = SeasonalContext.Detect(new DateTime(2025, 1, 1), new[] { NewYears() });

        Assert.True(ctx.MatchesHistorically(new DateTime(2022, 12, 30)));
    }

    [Fact]
    public void MatchesHistorically_WrappingWindow_MatchesJanuaryInPriorYear()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 1, 1), new[] { NewYears() });

        Assert.True(ctx.MatchesHistorically(new DateTime(2024, 1, 2)));
    }

    [Fact]
    public void MatchesHistorically_WrappingWindow_DoesNotMatchMidYear()
    {
        var ctx = SeasonalContext.Detect(new DateTime(2025, 1, 1), new[] { NewYears() });

        Assert.False(ctx.MatchesHistorically(new DateTime(2024, 6, 15)));
    }
}
