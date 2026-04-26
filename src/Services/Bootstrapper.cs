using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellyflix.Services;

/// <summary>
/// Runs once when the Jellyfin host starts. Performs self-checks and populates
/// <see cref="HealthState"/> so the admin config page can show a health badge.
///
/// Registered via <c>services.AddHostedService&lt;Bootstrapper&gt;()</c> in
/// <see cref="PluginServiceRegistrator"/>. In Jellyfin 10.11+ this is the correct
/// pattern — the older <c>IServerEntryPoint</c> interface was removed in 10.x.
///
/// If the startup log line ("Jellyflix … starting up") does not appear in
/// Jellyfin's log after install, verify that <c>IPluginServiceRegistrator</c> ran
/// before the host's service collection was frozen. The symptom is that
/// <c>RegisterServices</c> was invoked but the host had already built its
/// service provider; in that case, upgrade to the latest Jellyfin 10.11 point
/// release (the timing was fixed in 10.11.x).
/// </summary>
public class Bootstrapper : IHostedService
{
    private readonly ILogger<Bootstrapper> _log;
    private readonly HealthState           _health;
    private readonly ILibraryManager       _library;
    private readonly IUserManager          _users;

    public Bootstrapper(
        ILogger<Bootstrapper> log,
        HealthState health,
        ILibraryManager library,
        IUserManager users)
    {
        _log     = log;
        _health  = health;
        _library = library;
        _users   = users;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _log.LogInformation("Jellyflix {Version} starting up", version);

        try
        {
            _health.AddCheck("plugin_loaded",   true,                 "Plugin DLL loaded into Jellyfin host.");
            _health.AddCheck("library_manager", _library is not null, "Library manager reachable.");

            var userCount = _users.Users.Count();
            _health.AddCheck(
                "users_available",
                userCount > 0,
                userCount > 0
                    ? $"{userCount} user(s) available."
                    : "No Jellyfin users found — create one in the admin dashboard.");

            _health.MarkReady(version);
            _log.LogInformation("Jellyflix ready (v{Version})", version);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jellyflix bootstrapper failed");
            _health.MarkError(ex.Message);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("Jellyflix shutting down");
        return Task.CompletedTask;
    }
}
