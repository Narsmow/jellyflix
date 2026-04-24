using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellyflix.Models;

/// <summary>
/// A single horizontal row on the homepage ("Continue Watching", "Because you watched X", etc.).
/// Flat DTO designed for easy JSON serialization to the frontend.
/// </summary>
public class Rail
{
    /// <summary>Stable machine-readable key. Useful for the frontend to know which rail this is.</summary>
    public string Key { get; set; } = "";

    /// <summary>What the user sees as the row header.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// The "why" shown behind the (i) icon next to the title. Transparency is a feature.
    /// e.g. "Because you watched Stranger Things", "You tend to watch comedies around Christmas"
    /// </summary>
    public string Explanation { get; set; } = "";

    /// <summary>
    /// Optional badge rendered next to the title. e.g. "COUNTER-SEASONAL" when the
    /// anti-pattern detector fires. Purely decorative but a nice reinforcement.
    /// </summary>
    public string? Badge { get; set; }

    /// <summary>
    /// The Jellyfin item IDs in display order. The frontend uses these to fetch
    /// full item metadata and image URLs via Jellyfin's native API — we don't
    /// duplicate that data here.
    /// </summary>
    public List<Guid> ItemIds { get; set; } = new();
}
