using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MdbListCollections.Configuration;

/// <summary>
/// One MDBList list mirrored into one collection.
/// </summary>
public class ListMapping
{
    /// <summary>
    /// Gets or sets the list, as entered by the user: an mdblist.com list URL,
    /// "user/slug", or a numeric list id.
    /// </summary>
    public string List { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection name. Empty uses the list's own name.</summary>
    public string CollectionName { get; set; } = string.Empty;
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the MDBList API key (mdblist.com → Preferences → API).</summary>
    public string MdbListApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the lists to mirror.</summary>
    public List<ListMapping> Lists { get; set; } = new();
}
