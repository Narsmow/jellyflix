using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyflix.Configuration;
using Jellyfin.Plugin.Jellyflix.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellyflix.Services;

/// <summary>
/// Top-level orchestrator: builds the list of rails for a given user by
/// combining native Jellyfin data (items, playback history) with our
/// seasonal/counter-seasonal logic.
///
/// NOTE on Jellyfin service access:
/// In a real plugin you inject `ILibraryManager`, `IUserManager`, and
/// `IUserDataManager` via constructor. This file keeps the interface
/// slim so the recommendation logic is readable in isolation — flesh
/// out the Jellyfin-specific data-fetching calls when you wire it into
/// the real queries.
///
/// We pass around Guid userId (not a User entity) to stay insulated from
/// which namespace the User type lives in — it moved between Jellyfin
/// versions and this way we just don't care.
/// </summary>
public class RecommendationEngine
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;

    public RecommendationEngine(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData)
    {
        _library = library;
        _users = users;
        _userData = userData;
    }

    public async Task<List<Rail>> BuildHomepageAsync(Guid userId)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Verify the user exists. We don't need the entity itself past this point.
        if (_users.GetUserById(userId) is null)
        {
            throw new InvalidOperationException($"Unknown user {userId}");
        }

        var rails = new List<Rail>();

        rails.Add(await BuildContinueWatchingAsync(userId, config));
        rails.Add(await BuildRightNowRailAsync(userId, config));   // ← the clever one
        rails.AddRange(await BuildBecauseYouWatchedAsync(userId));
        rails.Add(await BuildHiddenGemsAsync(userId));
        rails.Add(await BuildForYourProfileAsync(userId));

        // Drop any empty rails — nothing is worse than a "recommended" row of zero items.
        return rails.Where(r => r.ItemIds.Count > 0).ToList();
    }

    // ────────────────────────────────────────────────────────────
    //  The star of the show: Right Now rail
    // ────────────────────────────────────────────────────────────

    public async Task<Rail> BuildRightNowRailAsync(Guid userId, PluginConfiguration config)
    {
        var context = SeasonalContext.Detect(DateTime.UtcNow, config.SeasonalWindows);

        // Not in any window? Return a generic personalized rail under the same slot.
        if (context.Current is null)
        {
            return new Rail
            {
                Key = "right-now",
                Title = "Right now",
                Explanation = "A fresh pick based on what you've been watching lately.",
                ItemIds = await FetchPersonalizedAsync(userId, limit: 20)
            };
        }

        var window = context.Current;
        var allWatches = await FetchUserHistoryAsync(userId);
        var periodWatches = allWatches
            .Where(w => context.MatchesHistorically(w.WatchedAt))
            .ToList();

        var detector = new AntiPatternDetector(
            config.MinHistoryThreshold,
            config.AffinityThreshold,
            config.AversionThreshold);

        var analysis = detector.Analyze(
            periodWatches.Select(w => (IReadOnlyList<string>)w.Tags).ToList(),
            allWatches.Select(w => (IReadOnlyList<string>)w.Tags).ToList(),
            window.ExpectedTags);

        switch (analysis.Verdict)
        {
            case AntiPatternDetector.Verdict.ProSeasonal:
                return new Rail
                {
                    Key = "right-now",
                    Title = $"Perfect for {window.DisplayName}",
                    Explanation = $"You usually reach for {string.Join(", ", window.ExpectedTags.Take(2)).ToLower()} " +
                                  $"around {window.DisplayName.ToLower()} — more of the same.",
                    ItemIds = await FetchByTagsAsync(userId, window.ExpectedTags, limit: 20)
                };

            case AntiPatternDetector.Verdict.CounterSeasonal:
                return new Rail
                {
                    Key = "right-now",
                    Title = $"Your {window.DisplayName} vibe",
                    Badge = "COUNTER-SEASONAL",
                    Explanation = $"Most people want {window.ExpectedTags.FirstOrDefault()?.ToLower() ?? ""} " +
                                  $"right now. You don't. These match what you actually watch at this time of year.",
                    ItemIds = await FetchByTagsAsync(userId, analysis.PeriodSignature, limit: 20)
                };

            case AntiPatternDetector.Verdict.Neutral:
                // Blend: half expected-season, half baseline. Keeps things seasonal-flavored
                // without being pushy.
                var blended = (await FetchByTagsAsync(userId, window.ExpectedTags, limit: 10))
                    .Concat(await FetchPersonalizedAsync(userId, limit: 10))
                    .Distinct()
                    .Take(20)
                    .ToList();
                return new Rail
                {
                    Key = "right-now",
                    Title = $"For your {window.DisplayName}",
                    Explanation = $"A mix of {window.DisplayName.ToLower()} favourites and your usuals.",
                    ItemIds = blended
                };

            case AntiPatternDetector.Verdict.NotEnoughHistory:
            default:
                return new Rail
                {
                    Key = "right-now",
                    Title = $"Perfect for {window.DisplayName}",
                    Explanation = $"Seasonal picks from your library.",
                    ItemIds = await FetchByTagsAsync(userId, window.ExpectedTags, limit: 20)
                };
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Supporting rails — stubs to flesh out
    // ────────────────────────────────────────────────────────────

    private Task<Rail> BuildContinueWatchingAsync(Guid userId, PluginConfiguration config)
    {
        // TODO: query IUserDataManager for items with PlaybackPositionTicks > 0,
        // exclude items last played > AbandonedThresholdDays ago,
        // exclude items whose next episode is ready (bump to "Up Next" instead).
        return Task.FromResult(new Rail
        {
            Key = "continue-watching",
            Title = "Continue Watching",
            Explanation = "Pick up where you left off.",
            ItemIds = new List<Guid>()
        });
    }

    private Task<List<Rail>> BuildBecauseYouWatchedAsync(Guid userId)
    {
        // TODO: take the last 2–3 COMPLETED items and build a "Because you watched X" rail
        // per item, using Jellyfin's built-in similarity or your own embedding.
        return Task.FromResult(new List<Rail>());
    }

    private Task<Rail> BuildHiddenGemsAsync(Guid userId)
    {
        // TODO: items with high CommunityRating or CriticRating that have zero playback
        // by this user and aren't already surfaced on another rail.
        return Task.FromResult(new Rail
        {
            Key = "hidden-gems",
            Title = "Hidden gems in your library",
            Explanation = "Highly rated, you've never watched.",
            ItemIds = new List<Guid>()
        });
    }

    private Task<Rail> BuildForYourProfileAsync(Guid userId)
    {
        return Task.FromResult(new Rail
        {
            Key = "for-your-profile",
            Title = "For you",
            Explanation = "Based on your overall watching habits.",
            ItemIds = new List<Guid>()
        });
    }

    // ────────────────────────────────────────────────────────────
    //  Data access stubs (wire to Jellyfin services in real implementation)
    // ────────────────────────────────────────────────────────────

    private class WatchRecord
    {
        public Guid ItemId { get; set; }
        public DateTime WatchedAt { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    private Task<List<WatchRecord>> FetchUserHistoryAsync(Guid userId)
    {
        // TODO: use _userData.GetAllUserData(userId) + _library.GetItemById() to join
        // playback history against items and flatten genres + tags into a per-watch
        // tag bag. Filter to items that were actually completed (e.g. >90% played).
        return Task.FromResult(new List<WatchRecord>());
    }

    private Task<List<Guid>> FetchByTagsAsync(Guid userId, IEnumerable<string> tags, int limit)
    {
        // TODO: _library.GetItemsResult(new InternalItemsQuery {
        //   User = _users.GetUserById(userId), Tags = tags.ToArray(), Limit = limit,
        //   OrderBy = by rating + recency blend
        // }).Items.Select(i => i.Id);
        return Task.FromResult(new List<Guid>());
    }

    private Task<List<Guid>> FetchPersonalizedAsync(Guid userId, int limit)
    {
        // TODO: simplest implementation — get the user's top genres from
        // completed items, query for highest-rated items in those genres
        // the user hasn't watched.
        return Task.FromResult(new List<Guid>());
    }
}
