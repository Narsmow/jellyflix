using Jellyfin.Plugin.Jellyflix.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellyflix.Api;

/// <summary>
/// GET /Jellyflix/Health
/// No auth required — the admin config page calls this right after install
/// to render a green "running" badge, before the user has necessarily
/// signed into the web UI. Only exposes status, never user data.
/// </summary>
[ApiController]
[Route("Jellyflix")]
public class HealthController : ControllerBase
{
    private readonly HealthState _health;

    public HealthController(HealthState health)
    {
        _health = health;
    }

    [HttpGet("Health")]
    public ActionResult<object> Get() => Ok(_health.Snapshot());
}
