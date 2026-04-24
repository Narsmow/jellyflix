using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jellyflix.Services;

/// <summary>
/// Classifies a user's seasonal behavior. Given their history during a
/// seasonal window and the tags we *expected* them to watch, returns whether
/// they lean into the season, avoid it, or land somewhere in between — and
/// what their actual period signature is if they avoid it.
/// </summary>
public class AntiPatternDetector
{
    private readonly int _minHistory;
    private readonly double _affinity;
    private readonly double _aversion;

    public AntiPatternDetector(int minHistory, double affinityThreshold, double aversionThreshold)
    {
        _minHistory = minHistory;
        _affinity = affinityThreshold;
        _aversion = aversionThreshold;
    }

    public enum Verdict
    {
        NotEnoughHistory,   // fall back to a mild seasonal nudge
        ProSeasonal,        // lean hard into expected
        CounterSeasonal,    // pivot to user's actual period signature
        Neutral             // blend
    }

    public class Analysis
    {
        public Verdict Verdict { get; set; }

        /// <summary>How strongly did past period watches overlap with expected tags? 0–1.</summary>
        public double AffinityScore { get; set; }

        /// <summary>
        /// For CounterSeasonal verdicts: the tags the user actually prefers during this
        /// window, with their year-round baseline subtracted out. These are the tags
        /// that make this period *distinct* for this user.
        /// </summary>
        public List<string> PeriodSignature { get; set; } = new();
    }

    /// <summary>
    /// Each item is represented as a bag of tag strings (genres, keywords, whatever
    /// you choose to feed in — normalize casing before calling).
    /// </summary>
    public Analysis Analyze(
        IReadOnlyList<IReadOnlyList<string>> periodWatches,
        IReadOnlyList<IReadOnlyList<string>> allWatches,
        IReadOnlyCollection<string> expectedTags)
    {
        if (periodWatches.Count < _minHistory)
        {
            return new Analysis { Verdict = Verdict.NotEnoughHistory };
        }

        double affinity = TagAffinity(periodWatches, expectedTags);

        if (affinity >= _affinity)
        {
            return new Analysis
            {
                Verdict = Verdict.ProSeasonal,
                AffinityScore = affinity
            };
        }

        if (affinity <= _aversion)
        {
            // Find what the user DOES watch during this window, minus their
            // baseline. The remainder is their period-specific signature.
            var periodTopTags = DominantTags(periodWatches, topN: 15);
            var baselineTopTags = DominantTags(allWatches, topN: 30);

            var signature = periodTopTags
                .Where(t => !baselineTopTags.Contains(t))
                .Take(8)
                .ToList();

            // If the subtraction left us empty (user's period behavior matches
            // their baseline exactly — they just don't do seasonal), fall back
            // to their period top tags.
            if (signature.Count == 0)
            {
                signature = periodTopTags.Take(8).ToList();
            }

            return new Analysis
            {
                Verdict = Verdict.CounterSeasonal,
                AffinityScore = affinity,
                PeriodSignature = signature
            };
        }

        return new Analysis
        {
            Verdict = Verdict.Neutral,
            AffinityScore = affinity
        };
    }

    // ────────────────────────────────────────────────────────────
    //  Math helpers
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fraction of period watches that contain at least one of the expected tags.
    /// Simple, robust, easy to reason about. Could swap in cosine similarity or
    /// TF-IDF later if needed.
    /// </summary>
    private static double TagAffinity(
        IReadOnlyList<IReadOnlyList<string>> watches,
        IReadOnlyCollection<string> expectedTags)
    {
        if (watches.Count == 0 || expectedTags.Count == 0) return 0.0;

        var expectedSet = new HashSet<string>(expectedTags, System.StringComparer.OrdinalIgnoreCase);
        int matches = watches.Count(w => w.Any(tag => expectedSet.Contains(tag)));
        return (double)matches / watches.Count;
    }

    /// <summary>Top-N most frequent tags across a set of watches.</summary>
    private static List<string> DominantTags(
        IReadOnlyList<IReadOnlyList<string>> watches,
        int topN)
    {
        return watches
            .SelectMany(w => w)
            .GroupBy(t => t, System.StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(topN)
            .Select(g => g.Key)
            .ToList();
    }
}
