using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ReidentifyByTmdb.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ReidentifyByTmdb;

/// <summary>
/// Plugin entry point. The work happens in <see cref="ReidentifyService"/>.
/// </summary>
/// <remarks>
/// Derives from the generic base class because only its constructor fills in Version and
/// AssemblyFilePath; the server dereferences Version while loading the plugin.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Reidentify by TMDB ID";

    /// <inheritdoc />
    public override string Description =>
        "Re-runs Identify on each movie and series, once, using the TMDB ID already stored on the item.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("bd87d941-d530-4dc0-8ab4-7e720426290b");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        ];
    }
}
