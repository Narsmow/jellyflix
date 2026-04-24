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
/// Runs once when the Jellyfin host starts up. Responsible for:
///   1. Logging that we loaded (proves install worked)
///   2. Performing self-checks and populating HealthState
///   3. Any first-run setup (cache dirs, migrations, optional downloads)
///
/// If the user ever did need us to fetch something remote (a bundled
/// model file, extra fonts for offline mode, etc.), this is the place —
/// grab it over HTTP into Plugin.Instance.ApplicationPaths.DataPath.
/// Today we don't need any runtime downloads; everything ships in the
/// plugin zip.
/// </summary>
public class Bootstrapper : IHostedService
{
    private readonly ILogger<Bootstrapper> _log;
    private readonly HealthState _health;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    public Bootstrapper(
        ILogger<Bootstrapper> log,
        HealthState health,
        ILibraryManager library,
        IUserManager users)
    {
        _log = log;
        _health = health;
        _library = library;
        _users = users;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _log.LogInformation("Jellyflix {Version} starting up", version);

        try
        {
            // ── self-checks ─────────────────────────────────────────
            _health.AddCheck("plugin_loaded", true, "Plugin DLL loaded into Jellyfin host.");

            var userCount = _users.Users.Count();
            _health.AddCheck(
                "users_available",
                userCount > 0,
                userCount > 0
                    ? $"{userCount} user(s) available."
                    : "No Jellyfin users found — create one in the dashboard.");

            _health.AddCheck("library_manager", _library is not null, "Library manager reachable.");

            // Placeholder for any first-run download logic. If in the
            // future we ship an optional resource (e.g. a large embedding
            // model) that we don't want inside the plugin zip, fetch it
            // here on first run and cache under ApplicationPaths.DataPath.
            //
            // await DownloadOptionalResourcesAsync(cancellationToken);

            _health.MarkReady(version);
            _log.LogInformation("Jellyflix ready.");
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
