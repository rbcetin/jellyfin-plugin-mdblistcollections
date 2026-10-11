using System.Globalization;
using Jellyfin.Plugin.MdbListCollections.Configuration;
using Jellyfin.Plugin.MdbListCollections.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MdbListCollections;

/// <summary>
/// MDBList list to collection plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ITaskManager _taskManager;

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    /// <param name="taskManager">Task manager.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ITaskManager taskManager)
        : base(applicationPaths, xmlSerializer)
    {
        _taskManager = taskManager;
        Instance = this;
    }

    /// <summary>Gets the singleton instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "MDBList Collections";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("654eb41b-7c10-4e71-8ca2-70ecfb29e0ed");

    /// <inheritdoc />
    public override string Description => "Keeps Jellyfin collections in sync with MDBList lists.";

    /// <inheritdoc />
    /// <remarks>Saving the config page syncs right away, so a new list or name shows up without waiting a day.</remarks>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        _taskManager.QueueScheduledTask<SyncCollectionsTask>();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        };
    }
}
