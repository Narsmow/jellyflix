using System.Collections.Generic;
using Jellyfin.Plugin.Jellyflix.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellyflix.Tests;

public class AntiPatternDetectorTests
{
    // Default thresholds matching PluginConfiguration defaults.
    private static AntiPatternDetector Default() => new(minHistory: 3, affinityThreshold: 0.6, aversionThreshold: 0.15);

    private static IReadOnlyList<string> Tags(params string[] tags) => tags;

    // ── NotEnoughHistory ───────────────────────────────────────────────────────

    [Fact]
    public void NotEnoughHistory_WhenNoPeriodWatches()
    {
        var result = Default().Analyze(
            periodWatches: new List<IReadOnlyList<string>>(),
            allWatches: new List<IReadOnlyList<string>> { Tags("Romance") },
            expectedTags: new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.NotEnoughHistory, result.Verdict);
    }

    [Fact]
    public void NotEnoughHistory_WhenBelowMinimumThreshold()
    {
        // 2 period watches, min is 3 — should be NotEnoughHistory.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Romance"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.NotEnoughHistory, result.Verdict);
    }

    [Fact]
    public void NotEnoughHistory_AtThresholdBoundary_IsNotTriggered()
    {
        // Exactly 3 period watches (== minHistory) should move past NotEnoughHistory.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Romance"),
            Tags("Romance"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.NotEqual(AntiPatternDetector.Verdict.NotEnoughHistory, result.Verdict);
    }

    // ── ProSeasonal ────────────────────────────────────────────────────────────

    [Fact]
    public void ProSeasonal_WhenAllItemsMatchExpectedTags()
    {
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance", "Drama"),
            Tags("Romantic Comedy"),
            Tags("Love", "Drama"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance", "Romantic Comedy", "Love" });

        Assert.Equal(AntiPatternDetector.Verdict.ProSeasonal, result.Verdict);
        Assert.Equal(1.0, result.AffinityScore, precision: 10);
    }

    [Fact]
    public void ProSeasonal_AtExactAffinityThreshold()
    {
        // 3 of 5 items match expected tags → affinity = 0.6 exactly.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Romance"),
            Tags("Romance"),
            Tags("Drama"),
            Tags("Action"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.ProSeasonal, result.Verdict);
        Assert.Equal(0.6, result.AffinityScore, precision: 10);
    }

    [Fact]
    public void ProSeasonal_AffinityScoreIsCorrect()
    {
        // 4 of 5 match → 0.8
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Horror"),
            Tags("Horror"),
            Tags("Horror"),
            Tags("Horror"),
            Tags("Comedy"),
        };

        var result = Default().Analyze(period, period, new[] { "Horror" });

        Assert.Equal(AntiPatternDetector.Verdict.ProSeasonal, result.Verdict);
        Assert.Equal(0.8, result.AffinityScore, precision: 10);
    }

    // ── CounterSeasonal ────────────────────────────────────────────────────────

    [Fact]
    public void CounterSeasonal_WhenNoItemsMatchExpectedTags()
    {
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance", "Love" });

        Assert.Equal(AntiPatternDetector.Verdict.CounterSeasonal, result.Verdict);
        Assert.Equal(0.0, result.AffinityScore, precision: 10);
    }

    [Fact]
    public void CounterSeasonal_AtExactAversionThreshold()
    {
        // 1 of 7 items matches → ≈ 0.143, which is <= 0.15.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.CounterSeasonal, result.Verdict);
    }

    [Fact]
    public void CounterSeasonal_SignatureExcludesBaselineTags()
    {
        // Year-round baseline: only Action.
        // During the window: SciFi + Action. SciFi never appears outside the period,
        // so the subtraction (periodTop minus baselineTop) keeps SciFi and drops Action.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Action", "SciFi"),
            Tags("Action", "SciFi"),
            Tags("SciFi"),
        };

        // allWatches = Action only (SciFi absent from baseline).
        var allWatches = new List<IReadOnlyList<string>>();
        for (int i = 0; i < 30; i++) allWatches.Add(Tags("Action"));

        var result = Default().Analyze(period, allWatches, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.CounterSeasonal, result.Verdict);
        // SciFi is period-specific (not in baseline); Action is year-round so it's subtracted out.
        Assert.Contains("SciFi", result.PeriodSignature);
        Assert.DoesNotContain("Action", result.PeriodSignature);
    }

    [Fact]
    public void CounterSeasonal_FallsBackToPeriodTopTags_WhenSignatureIsEmpty()
    {
        // Period behavior exactly mirrors baseline — subtraction yields nothing.
        // Should fall back to the raw period-top tags.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
        };

        var allWatches = new List<IReadOnlyList<string>>
        {
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
        };

        var result = Default().Analyze(period, allWatches, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.CounterSeasonal, result.Verdict);
        Assert.NotEmpty(result.PeriodSignature);
        Assert.Contains("Action", result.PeriodSignature);
    }

    [Fact]
    public void CounterSeasonal_SignatureCapAt8Tags()
    {
        // 10 distinct period-specific tags — signature must be capped at 8.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T9", "T10"),
            Tags("T1"),
            Tags("T1"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.CounterSeasonal, result.Verdict);
        Assert.True(result.PeriodSignature.Count <= 8);
    }

    // ── Neutral ────────────────────────────────────────────────────────────────

    [Fact]
    public void Neutral_WhenAffinityBetweenThresholds()
    {
        // 2 of 6 match → affinity ≈ 0.333, which is between 0.15 and 0.6.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Romance"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
            Tags("Action"),
        };

        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.Neutral, result.Verdict);
        Assert.InRange(result.AffinityScore, 0.16, 0.59);
    }

    // ── Case sensitivity ───────────────────────────────────────────────────────

    [Fact]
    public void TagComparison_IsCaseInsensitive()
    {
        var period = new List<IReadOnlyList<string>>
        {
            Tags("romance"),  // lower-case
            Tags("ROMANCE"),  // upper-case
            Tags("Romance"),  // title-case
        };

        // expectedTags uses title-case — all three above should match.
        var result = Default().Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.ProSeasonal, result.Verdict);
        Assert.Equal(1.0, result.AffinityScore, precision: 10);
    }

    // ── Edge cases ─────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyExpectedTags_YieldsZeroAffinity()
    {
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Romance"),
            Tags("Romance"),
        };

        var result = Default().Analyze(period, period, expectedTags: System.Array.Empty<string>());

        // Affinity = 0 → CounterSeasonal (since 0 <= aversionThreshold).
        Assert.Equal(AntiPatternDetector.Verdict.CounterSeasonal, result.Verdict);
        Assert.Equal(0.0, result.AffinityScore, precision: 10);
    }

    [Fact]
    public void CustomThresholds_AreRespected()
    {
        // tighter thresholds: affinity=0.8, aversion=0.4
        var detector = new AntiPatternDetector(minHistory: 2, affinityThreshold: 0.8, aversionThreshold: 0.4);

        // 3 of 5 match → 0.6, which is between 0.4 and 0.8 → Neutral.
        var period = new List<IReadOnlyList<string>>
        {
            Tags("Romance"),
            Tags("Romance"),
            Tags("Romance"),
            Tags("Action"),
            Tags("Action"),
        };

        var result = detector.Analyze(period, period, new[] { "Romance" });

        Assert.Equal(AntiPatternDetector.Verdict.Neutral, result.Verdict);
    }
}
