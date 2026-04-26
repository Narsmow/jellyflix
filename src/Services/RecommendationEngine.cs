using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellyflix.Configuration;
using Jellyfin.Plugin.Jellyflix.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellyflix.Services;

/// <summary>
/// Orchestrates all homepage rails for a given user, combining Jellyfin library
/// data with the seasonal/counter-seasonal detection logic.
///
/// Registered as a singleton so the per-user rail cache lives for the host lifetime.
/// </summary>
public class RecommendationEngine
{
    // ── Configuration constants ─────────────────────────────────────────────────

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private const double HiddenGemMinRating         = 7.0;
    private const int    BecauseYouWatchedMaxRails  = 3;
    private const int    TopGenresForPersonalization = 5;
    private const int    NewlyAddedDays              = 30;

    /// <summary>
    /// Cap on how many played items we load per user for pattern analysis.
    /// Covers 2–3 years of typical viewing without unbounded DB reads.
    /// </summary>
    private const int HistoryFetchLimit = 500;

    /// <summary>
    /// Hard cap on how many users we keep cached rails for at once.
    /// Plugin's expected scale is single-digit-to-dozens of users; this prevents
    /// pathological growth (e.g. a buggy client iterating user IDs).
    /// </summary>
    private const int CacheMaxEntries = 256;

    // ── Per-user cache ──────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<Guid, (DateTime ExpiresAt, List<Rail> Rails)>
        _cache = new();

    // ── Dependencies ────────────────────────────────────────────────────────────

    private readonly ILibraryManager  _library;
    private readonly IUserManager     _users;
    private readonly IUserDataManager _userData;

    public RecommendationEngine(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData)
    {
        _library  = library;
        _users    = users;
        _userData = userData;
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  Public API
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<List<Rail>> BuildHomepageAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Rails;
        }

        var user   = _users.GetUserById(userId)
            ?? throw new InvalidOperationException($"Unknown user {userId}");
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Fetch history once; pass it through to every rail builder that needs it.
        // Prevents repeated DB queries across the request lifetime.
        var history = FetchHistory(user);

        var rails = new List<Rail>();

        rails.Add(await BuildContinueWatchingRailAsync(user, config, ct));
        rails.Add(await BuildRightNowRailAsync(user, config, history, ct));
        rails.AddRange(await BuildBecauseYouWatchedRailsAsync(user, ct));
        rails.Add(await BuildNewToLibraryRailAsync(user, history, ct));
        rails.Add(await BuildHiddenGemsRailAsync(user, ct));
        rails.Add(await BuildForYourProfileRailAsync(user, history, ct));

        var result = rails.Where(r => r.ItemIds.Count > 0).ToList();
        StoreInCache(userId, result);
        return result;
    }

    public static void InvalidateCache(Guid userId) => _cache.TryRemove(userId, out _);

    /// <summary>Drops every cached entry. Called when the plugin's config changes.</summary>
    public static void InvalidateAllCaches() => _cache.Clear();

    private static void StoreInCache(Guid userId, List<Rail> rails)
    {
        // Bound the dictionary. If we're at the cap, evict the oldest expired
        // entry; if none are expired, evict an arbitrary entry to make room.
        if (_cache.Count >= CacheMaxEntries)
        {
            var now = DateTime.UtcNow;
            var victim = _cache
                .Where(kv => kv.Value.ExpiresAt <= now)
                .Select(kv => (Guid?)kv.Key)
                .FirstOrDefault()
                ?? _cache.Keys.FirstOrDefault();

            if (victim is Guid id) _cache.TryRemove(id, out _);
        }

        _cache[userId] = (DateTime.UtcNow + CacheTtl, rails);
    }

    /// <summary>Per-window affinity breakdown for the Profile/Insights page.</summary>
    public object BuildInsights(Guid userId)
    {
        var user = _users.GetUserById(userId)
            ?? throw new InvalidOperationException($"Unknown user {userId}");

        var config  = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var history = FetchHistory(user);
        var detector = new AntiPatternDetector(
            config.MinHistoryThreshold,
            config.AffinityThreshold,
            config.AversionThreshold);

        var windows = config.SeasonalWindows.Select(w =>
        {
            var ctx    = new SeasonalContext(w);
            var period = history.Where(h => ctx.MatchesHistorically(h.WatchedAt)).ToList();
            var analysis = detector.Analyze(
                period.Select(h => (IReadOnlyList<string>)h.Tags).ToList(),
                history.Select(h => (IReadOnlyList<string>)h.Tags).ToList(),
                w.ExpectedTags);

            return new
            {
                window          = w.Key,
                displayName     = w.DisplayName,
                periodWatches   = period.Count,
                affinity        = Math.Round(analysis.AffinityScore, 3),
                verdict         = analysis.Verdict.ToString(),
                periodSignature = analysis.PeriodSignature
            };
        }).ToList(); // materialise before returning so history isn't captured lazily

        var current = SeasonalContext.Detect(DateTime.UtcNow, config.SeasonalWindows);
        var topTags = history
            .SelectMany(h => h.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new { tag = g.Key, count = g.Count() })
            .ToList();

        return new
        {
            userId,
            totalWatched  = history.Count,
            currentWindow = current.Current?.Key,
            topTags,
            windows
        };
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  Rail builders
    // ────────────────────────────────────────────────────────────────────────────

    public async Task<Rail> BuildRightNowRailAsync(
        User user,
        PluginConfiguration config,
        List<WatchRecord> history,
        CancellationToken ct = default)
    {
        var context = SeasonalContext.Detect(DateTime.UtcNow, config.SeasonalWindows);

        if (context.Current is null)
        {
            return new Rail
            {
                Key         = "right-now",
                Title       = "Right now",
                Explanation = "A fresh pick based on what you've been watching lately.",
                ItemIds     = await FetchPersonalizedAsync(user, history, limit: 20, ct)
            };
        }

        var window = context.Current;
        var period = history.Where(w => context.MatchesHistorically(w.WatchedAt)).ToList();

        var detector = new AntiPatternDetector(
            config.MinHistoryThreshold,
            config.AffinityThreshold,
            config.AversionThreshold);

        var analysis = detector.Analyze(
            period.Select(w => (IReadOnlyList<string>)w.Tags).ToList(),
            history.Select(w => (IReadOnlyList<string>)w.Tags).ToList(),
            window.ExpectedTags);

        return analysis.Verdict switch
        {
            AntiPatternDetector.Verdict.ProSeasonal => new Rail
            {
                Key         = "right-now",
                Title       = $"Perfect for {window.DisplayName}",
                Explanation = $"You usually reach for {Describe(window.ExpectedTags)} " +
                              $"around {window.DisplayName.ToLowerInvariant()} — more of the same.",
                ItemIds     = await FetchByTagsAsync(user, window.ExpectedTags, limit: 20, ct)
            },

            AntiPatternDetector.Verdict.CounterSeasonal => new Rail
            {
                Key         = "right-now",
                Title       = $"Your {window.DisplayName} vibe",
                Badge       = "COUNTER-SEASONAL",
                Explanation = $"Most people want {window.ExpectedTags.FirstOrDefault()?.ToLowerInvariant() ?? "seasonal content"} " +
                              "right now. You don't. These match what you actually watch at this time of year.",
                ItemIds     = await FetchByTagsAsync(user, analysis.PeriodSignature, limit: 20, ct)
            },

            AntiPatternDetector.Verdict.Neutral => new Rail
            {
                Key         = "right-now",
                Title       = $"For your {window.DisplayName}",
                Explanation = $"A mix of {window.DisplayName.ToLowerInvariant()} favourites and your usuals.",
                ItemIds     = (await FetchByTagsAsync(user, window.ExpectedTags, limit: 10, ct))
                                .Concat(await FetchPersonalizedAsync(user, history, limit: 10, ct))
                                .Distinct()
                                .Take(20)
                                .ToList()
            },

            _ => new Rail // NotEnoughHistory
            {
                Key         = "right-now",
                Title       = $"Perfect for {window.DisplayName}",
                Explanation = "Seasonal picks from your library.",
                ItemIds     = await FetchByTagsAsync(user, window.ExpectedTags, limit: 20, ct)
            }
        };
    }

    private Task<Rail> BuildContinueWatchingRailAsync(
        User user,
        PluginConfiguration config,
        CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-config.AbandonedThresholdDays);

        // Fetch a bounded set; the loop caps at 15 anyway but don't stream
        // an unbounded query from the DB when the user might have hundreds
        // of in-progress items.
        var items = _library.GetItemsResult(new InternalItemsQuery
        {
            User             = user,
            IsResumable      = true,
            Recursive        = true,
            Limit            = 50,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            OrderBy          = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
        }).Items;

        var seen   = new HashSet<string>();
        var result = new List<Guid>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var data = _userData.GetUserData(user, item);
            if (data is null || !data.LastPlayedDate.HasValue || data.LastPlayedDate.Value < cutoff)
            {
                continue;
            }

            // Collapse episodes: one entry per series.
            var groupKey = !string.IsNullOrEmpty(item.ExternalSeriesId) ? item.ExternalSeriesId
                         : item.ParentId != Guid.Empty                  ? item.ParentId.ToString()
                         : item.Id.ToString();

            if (!seen.Add(groupKey)) continue;

            result.Add(item.Id);
            if (result.Count >= 15) break;
        }

        return Task.FromResult(new Rail
        {
            Key         = "continue-watching",
            Title       = "Continue Watching",
            Explanation = $"Pick up where you left off. Anything idle for more than " +
                          $"{config.AbandonedThresholdDays} days is hidden.",
            ItemIds     = result
        });
    }

    private Task<List<Rail>> BuildBecauseYouWatchedRailsAsync(User user, CancellationToken ct)
    {
        var anchors = _library.GetItemsResult(new InternalItemsQuery
        {
            User             = user,
            IsPlayed         = true,
            Recursive        = true,
            Limit            = BecauseYouWatchedMaxRails,
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            OrderBy          = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
        }).Items;

        var rails = new List<Rail>();

        foreach (var anchor in anchors)
        {
            ct.ThrowIfCancellationRequested();
            if (anchor.Genres.Length == 0) continue;

            var ids = _library.GetItemsResult(new InternalItemsQuery
            {
                User             = user,
                Genres           = anchor.Genres,
                IsPlayed         = false,
                Recursive        = true,
                Limit            = 20,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                OrderBy          = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) }
            }).Items
            .Where(i => i.Id != anchor.Id)
            .Select(i => i.Id)
            .ToList();

            if (ids.Count == 0) continue;

            rails.Add(new Rail
            {
                Key         = $"because-{anchor.Id}",
                Title       = $"Because you watched {anchor.Name}",
                Explanation = $"Similar in genre ({Describe(anchor.Genres.Take(2))}) — " +
                              "pulled from your library's metadata.",
                ItemIds     = ids
            });
        }

        return Task.FromResult(rails);
    }

    private Task<Rail> BuildNewToLibraryRailAsync(
        User user,
        List<WatchRecord> history,
        CancellationToken ct)
    {
        // Top genres from the user's history; if none yet, fall back to no filter.
        var topGenres = history
            .SelectMany(w => w.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(TopGenresForPersonalization)
            .Select(g => g.Key)
            .ToArray();

        var query = new InternalItemsQuery
        {
            User             = user,
            IsPlayed         = false,
            Recursive        = true,
            Limit            = 20,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            OrderBy          = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            // Filter to recently-added items (server-side via MinDateLastSaved
            // isn't exposed on InternalItemsQuery, so we filter client-side below).
        };

        if (topGenres.Length > 0) query.Genres = topGenres;

        var cutoff = DateTime.UtcNow.AddDays(-NewlyAddedDays);
        var ids = _library.GetItemsResult(query)
            .Items
            .Where(i => i.DateCreated >= cutoff)
            .Select(i => i.Id)
            .Take(20)
            .ToList();

        var explanation = topGenres.Length > 0
            ? $"Recently added to your library, filtered to your usual genres ({Describe(topGenres)})."
            : $"Added in the last {NewlyAddedDays} days.";

        return Task.FromResult(new Rail
        {
            Key         = "new-to-library",
            Title       = "New to your library",
            Explanation = explanation,
            ItemIds     = ids
        });
    }

    private Task<Rail> BuildHiddenGemsRailAsync(User user, CancellationToken ct)
    {
        var ids = _library.GetItemsResult(new InternalItemsQuery
        {
            User               = user,
            IsPlayed           = false,
            Recursive          = true,
            MinCommunityRating = HiddenGemMinRating,
            IncludeItemTypes   = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            OrderBy            = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
            Limit              = 20
        }).Items.Select(i => i.Id).ToList();

        return Task.FromResult(new Rail
        {
            Key         = "hidden-gems",
            Title       = "Hidden gems in your library",
            Explanation = $"Rated ≥{HiddenGemMinRating} and you've never played them.",
            ItemIds     = ids
        });
    }

    private async Task<Rail> BuildForYourProfileRailAsync(
        User user,
        List<WatchRecord> history,
        CancellationToken ct)
    {
        return new Rail
        {
            Key         = "for-your-profile",
            Title       = "For you",
            Explanation = "Based on your overall watching habits.",
            ItemIds     = await FetchPersonalizedAsync(user, history, limit: 20, ct)
        };
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  Data access
    // ────────────────────────────────────────────────────────────────────────────

    public record WatchRecord(Guid ItemId, DateTime WatchedAt, List<string> Tags);

    public List<WatchRecord> FetchHistory(User user)
    {
        var items = _library.GetItemsResult(new InternalItemsQuery
        {
            User             = user,
            IsPlayed         = true,
            Recursive        = true,
            Limit            = HistoryFetchLimit,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            OrderBy          = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
        }).Items;

        var records = new List<WatchRecord>(items.Count);
        foreach (var item in items)
        {
            var data = _userData.GetUserData(user, item);
            if (data is null || !data.LastPlayedDate.HasValue) continue;

            var tags = new List<string>(item.Genres.Length + item.Tags.Length);
            tags.AddRange(item.Genres);
            tags.AddRange(item.Tags);

            records.Add(new WatchRecord(item.Id, data.LastPlayedDate.Value, tags));
        }

        return records;
    }

    private Task<List<Guid>> FetchByTagsAsync(
        User user,
        IEnumerable<string> tags,
        int limit,
        CancellationToken ct)
    {
        var tagArray = tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        if (tagArray.Length == 0) return Task.FromResult(new List<Guid>());

        // Jellyfin Tags queries use AND semantics. To get OR across the tag set,
        // query each tag separately and union the results.
        var seen   = new HashSet<Guid>();
        var result = new List<Guid>();

        foreach (var tag in tagArray)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count >= limit) break;

            var items = _library.GetItemsResult(new InternalItemsQuery
            {
                User             = user,
                Tags             = new[] { tag },
                IsPlayed         = false,
                Recursive        = true,
                Limit            = limit,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                OrderBy          = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) }
            }).Items;

            foreach (var item in items)
            {
                if (seen.Add(item.Id)) result.Add(item.Id);
                if (result.Count >= limit) break;
            }
        }

        // Fallback: try the same strings as genres (many libraries don't use Tags).
        if (result.Count == 0)
        {
            result = _library.GetItemsResult(new InternalItemsQuery
            {
                User             = user,
                Genres           = tagArray,
                IsPlayed         = false,
                Recursive        = true,
                Limit            = limit,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                OrderBy          = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) }
            }).Items.Select(i => i.Id).ToList();
        }

        return Task.FromResult(result);
    }

    private Task<List<Guid>> FetchPersonalizedAsync(
        User user,
        List<WatchRecord> history,
        int limit,
        CancellationToken ct)
    {
        var topGenres = history
            .SelectMany(w => w.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(TopGenresForPersonalization)
            .Select(g => g.Key)
            .ToArray();

        var query = topGenres.Length > 0
            ? new InternalItemsQuery
            {
                User             = user,
                Genres           = topGenres,
                IsPlayed         = false,
                Recursive        = true,
                Limit            = limit,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                OrderBy          = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) }
            }
            : new InternalItemsQuery
            {
                User             = user,
                IsPlayed         = false,
                Recursive        = true,
                Limit            = limit,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                OrderBy          = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) }
            };

        return Task.FromResult(
            _library.GetItemsResult(query).Items.Select(i => i.Id).ToList());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string Describe(IEnumerable<string> tags) =>
        string.Join(", ", tags.Take(2).Select(t => t.ToLowerInvariant()));
}
