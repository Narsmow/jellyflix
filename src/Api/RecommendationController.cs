using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellyflix.Models;
using Jellyfin.Plugin.Jellyflix.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellyflix.Api;

[ApiController]
[Authorize(Policy = Policies.FirstTimeSetupOrDefault)]
[Route("Jellyflix")]
public class RecommendationController : ControllerBase
{
    private readonly RecommendationEngine  _engine;
    private readonly IAuthorizationContext _authCtx;
    private readonly IUserManager          _users;

    public RecommendationController(
        RecommendationEngine engine,
        IAuthorizationContext authCtx,
        IUserManager users)
    {
        _engine  = engine;
        _authCtx = authCtx;
        _users   = users;
    }

    /// <summary>GET /Jellyflix/Rails/{userId}</summary>
    [HttpGet("Rails/{userId}")]
    public async Task<ActionResult<List<Rail>>> GetRails(
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForUserAsync(userId)) return Forbid();

        try
        {
            var rails = await _engine.BuildHomepageAsync(userId, cancellationToken);
            return Ok(rails);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>DELETE /Jellyflix/Rails/{userId}/cache — force-refresh a user's cache.</summary>
    [HttpDelete("Rails/{userId}/cache")]
    public async Task<IActionResult> InvalidateCache(Guid userId)
    {
        if (!await IsAuthorizedForUserAsync(userId)) return Forbid();

        RecommendationEngine.InvalidateCache(userId);
        return NoContent();
    }

    /// <summary>GET /Jellyflix/Profile/Insights/{userId}</summary>
    [HttpGet("Profile/Insights/{userId}")]
    public async Task<ActionResult<object>> GetInsights(Guid userId)
    {
        if (!await IsAuthorizedForUserAsync(userId)) return Forbid();

        try
        {
            return Ok(_engine.BuildInsights(userId));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Allows the request only if the authenticated user owns <paramref name="userId"/>
    /// or is a Jellyfin administrator. Prevents cross-user data leakage.
    /// </summary>
    private async Task<bool> IsAuthorizedForUserAsync(Guid userId)
    {
        var authInfo = await _authCtx.GetAuthorizationInfo(HttpContext);
        if (!authInfo.IsAuthenticated) return false;
        if (authInfo.UserId == userId)  return true;

        var requestingUser = _users.GetUserById(authInfo.UserId);
        return requestingUser is not null
            && requestingUser.HasPermission(PermissionKind.IsAdministrator);
    }
}
