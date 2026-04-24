using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellyflix.Api;

/// <summary>
/// Serves the web frontend embedded inside the plugin DLL.
///   /Jellyflix/Web/           → index.html
///   /Jellyflix/Web/app.css    → assets/app.css
///   /Jellyflix/Web/app.js     → assets/app.js
///
/// Everything comes from EmbeddedResource entries in the .csproj, so there
/// is no second artifact to host or download — the plugin zip is complete.
/// </summary>
[ApiController]
[Route("Jellyflix/Web")]
public class StaticFilesController : ControllerBase
{
    [HttpGet("")]
    [HttpGet("index.html")]
    public IActionResult Index() =>
        Serve("Jellyfin.Plugin.Jellyflix.web.index.html", "text/html; charset=utf-8");

    [HttpGet("assets/app.css")]
    public IActionResult Css() =>
        Serve("Jellyfin.Plugin.Jellyflix.web.assets.app.css", "text/css; charset=utf-8");

    [HttpGet("assets/app.js")]
    public IActionResult Js() =>
        Serve("Jellyfin.Plugin.Jellyflix.web.assets.app.js", "application/javascript; charset=utf-8");

    private IActionResult Serve(string resourceName, string contentType)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound(new { error = $"Embedded resource {resourceName} not found" });
        }
        // File() takes ownership of the stream and disposes it when done.
        return File(stream, contentType);
    }
}
