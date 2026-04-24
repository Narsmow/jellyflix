using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellyflix.Models;
using Jellyfin.Plugin.Jellyflix.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellyflix.Api;

/// <summary>
/// REST endpoints the web frontend calls. Mounted under /Jellyflix/…
/// Authorization policy "DefaultAuthorization" is Jellyfin's built-in —
/// requires a valid user token, just like the native API.
/// </summary>
[ApiController]
[Authorize(Policy = "DefaultAuthorization")]
[Route("Jellyflix")]
public class RecommendationController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;

    public RecommendationController(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData)
    {
        _library = library;
        _users = users;
        _userData = userData;
    }

    /// <summary>
    /// GET /Jellyflix/Rails/{userId}
    /// Returns the ordered list of rails for the homepage. The frontend uses
    /// the item IDs in each rail to fetch metadata/images via Jellyfin's
    /// native /Users/{id}/Items endpoint.
    /// </summary>
    [HttpGet("Rails/{userId}")]
    public async Task<ActionResult<System.Collections.Generic.List<Rail>>> GetRails(Guid userId)
    {
        var engine = new RecommendationEngine(_library, _users, _userData);
        var rails = await engine.BuildHomepageAsync(userId);
        return Ok(rails);
    }

    /// <summary>
    /// GET /Jellyflix/Profile/Insights/{userId}
    /// Returns explainer data for the profile stats page: seasonal affinity
    /// by window, top genres by time of year, counter-pattern detection, etc.
    /// Populate once the dashboard UI is built.
    /// </summary>
    [HttpGet("Profile/Insights/{userId}")]
    public ActionResult<object> GetInsights(Guid userId)
    {
        // TODO: return seasonal breakdown, most-watched tags per window,
        // and any detected counter-patterns. Good candidate for the Phase 4 roadmap.
        return Ok(new { todo = true, userId });
    }
}
