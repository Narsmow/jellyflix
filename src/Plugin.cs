using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellyflix.Configuration;
using Jellyfin.Plugin.Jellyflix.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellyflix;

/// <summary>
/// Main plugin entry point. Jellyfin discovers this class via reflection on load.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Drop every cached rail set when the admin saves new thresholds —
        // otherwise users would see stale recommendations until the per-user
        // TTL expires.
        ConfigurationChanged += (_, _) => RecommendationEngine.InvalidateAllCaches();
    }

    public override string Name => "Jellyflix";

    public override Guid Id => Guid.Parse("f2c9d8e4-3a1b-4c6d-9e7f-8a2b1c3d4e5f");

    public override string Description =>
        "A Netflix-style frontend with seasonal and counter-seasonal recommendations.";

    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Register the configuration page that Jellyfin's admin dashboard will serve.
    /// The HTML file is embedded in the plugin DLL (see .csproj, EmbeddedResource).
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    };
}
