using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ReidentifyByTmdb.Configuration;

/// <summary>
/// Plugin settings, edited on the plugin's settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether new movies and series are re-identified
    /// automatically after the library scan that added them.
    /// </summary>
    public bool EnableOnNewItems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether existing images are replaced as well as text metadata.
    /// </summary>
    public bool ReplaceImages { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to only log what would be done, without changing anything.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the libraries to process. Empty means all libraries.
    /// </summary>
#pragma warning disable CA1819 // Arrays are what the XML configuration serializer round-trips cleanly.
    public string[] LibraryIds { get; set; } = [];
#pragma warning restore CA1819
}
