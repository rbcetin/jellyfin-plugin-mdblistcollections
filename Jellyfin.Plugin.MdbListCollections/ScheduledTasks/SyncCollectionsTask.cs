using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MdbListCollections.ScheduledTasks;

/// <summary>
/// Mirrors every configured MDBList list into its collection: creates the collection if missing,
/// adds library items that are on the list, removes ones that dropped off it.
/// </summary>
public class SyncCollectionsTask : IScheduledTask
{
    /// <summary>
    /// Provider id stamped on each collection we own, holding the list reference. Collections are
    /// found by this, not by name, so renaming one in Jellyfin does not orphan it.
    /// </summary>
    private const string ListIdKey = "MdbList";

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly IImageEncoder _imageEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly MdbListClient _mdbList;
    private readonly ILogger<SyncCollectionsTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncCollectionsTask"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="collectionManager">Collection manager.</param>
    /// <param name="providerManager">Provider manager, for saving the collage.</param>
    /// <param name="imageEncoder">Image encoder, for building the collage.</param>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="mdbList">MDBList client.</param>
    /// <param name="logger">Logger.</param>
    public SyncCollectionsTask(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        IImageEncoder imageEncoder,
        IApplicationPaths appPaths,
        MdbListClient mdbList,
        ILogger<SyncCollectionsTask> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _imageEncoder = imageEncoder;
        _appPaths = appPaths;
        _mdbList = mdbList;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync MDBList collections";

    /// <inheritdoc />
    public string Key => "MdbListCollectionsSyncTask";

    /// <inheritdoc />
    public string Description => "Creates and updates collections from the configured MDBList lists.";

    /// <inheritdoc />
    public string Category => "MDBList Collections";

    /// <inheritdoc />
    /// <remarks>
    /// The task is the plugin's only mechanism, so it must carry real default triggers. Startup as
    /// well as daily, so a fresh install does not wait a day for its first sync.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Lists.Count == 0)
        {
            _logger.LogInformation("MdbListCollections: no lists configured, nothing to do");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.MdbListApiKey))
        {
            _logger.LogWarning("MdbListCollections: no MDBList API key set");
            progress.Report(100);
            return;
        }

        var index = BuildLibraryIndex();

        for (var i = 0; i < config.Lists.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapping = config.Lists[i];

            try
            {
                await SyncOneAsync(mapping.List, mapping.CollectionName, config.MdbListApiKey, index, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The collection is left untouched: a failed fetch must never look like an empty list.
                _logger.LogError(ex, "MdbListCollections: '{List}' skipped", mapping.List);
            }

            progress.Report((i + 1) * 100.0 / config.Lists.Count);
        }
    }

    private async Task SyncOneAsync(
        string listInput,
        string collectionName,
        string apiKey,
        Dictionary<string, List<Guid>> index,
        CancellationToken cancellationToken)
    {
        var listRef = MdbListClient.ParseListRef(listInput)
            ?? throw new ArgumentException($"not an MDBList list URL, user/slug or id: '{listInput}'");

        var list = await _mdbList.GetListAsync(listRef, apiKey, cancellationToken).ConfigureAwait(false);
        var configuredName = collectionName.Trim();

        // List order is kept so the collage shows the top of the list.
        var wanted = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var entry in list.Entries)
        {
            foreach (var key in Keys(entry))
            {
                if (index.TryGetValue(key, out var ids))
                {
                    wanted.AddRange(ids.Where(seen.Add));
                    break;
                }
            }
        }

        var boxSet = FindCollection(listRef, string.IsNullOrEmpty(configuredName) ? list.Name : configuredName);
        if (boxSet is null)
        {
            if (wanted.Count == 0)
            {
                _logger.LogInformation("MdbListCollections: '{List}': none of {Count} list items are in the library, not creating", list.Name, list.Entries.Count);
                return;
            }

            boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = string.IsNullOrEmpty(configuredName) ? list.Name : configuredName,
                ItemIdList = wanted.Select(g => g.ToString("N")).ToArray(),
            }).ConfigureAwait(false);

            _logger.LogInformation("MdbListCollections: '{Name}' created with {Count} of {Total} list items", boxSet.Name, wanted.Count, list.Entries.Count);
        }
        else
        {
            var current = boxSet.GetLinkedChildren().Select(c => c.Id).ToHashSet();
            var toAdd = wanted.Where(id => !current.Contains(id)).ToList();
            var toRemove = current.Where(id => !seen.Contains(id)).ToList();

            if (toAdd.Count > 0)
            {
                await _collectionManager.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);
            }

            if (toRemove.Count > 0)
            {
                await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, toRemove).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "MdbListCollections: '{Name}' synced, {Count} of {Total} list items in library (+{Added} -{Removed})",
                boxSet.Name,
                wanted.Count,
                list.Entries.Count,
                toAdd.Count,
                toRemove.Count);
        }

        // A configured name wins; an empty one leaves whatever the collection is called in Jellyfin.
        var changed = false;
        if (!string.IsNullOrEmpty(configuredName) && !string.Equals(boxSet.Name, configuredName, StringComparison.Ordinal))
        {
            _logger.LogInformation("MdbListCollections: renaming '{Old}' to '{New}'", boxSet.Name, configuredName);
            boxSet.Name = configuredName;
            changed = true;
        }

        if (!string.Equals(boxSet.GetProviderId(ListIdKey), listRef, StringComparison.Ordinal))
        {
            boxSet.SetProviderId(ListIdKey, listRef);
            changed = true;
        }

        if (changed)
        {
            await boxSet.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        await EnsureCollageAsync(boxSet, wanted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the collection a poster collage of the top list items -- only when it has no poster,
    /// so an image set by hand is never replaced. Delete the poster to get a fresh collage.
    /// </summary>
    private async Task EnsureCollageAsync(BoxSet boxSet, List<Guid> wanted, CancellationToken cancellationToken)
    {
        if (boxSet.HasImage(ImageType.Primary, 0) || !_imageEncoder.SupportsImageCollageCreation)
        {
            return;
        }

        var posters = wanted
            .Select(id => _libraryManager.GetItemById(id)?.GetImageInfo(ImageType.Primary, 0))
            .Where(img => img is not null && img.IsLocalFile && File.Exists(img.Path))
            .Select(img => img!.Path)
            .Take(4)
            .ToList();

        if (posters.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(_appPaths.TempDirectory);
        var output = Path.Combine(_appPaths.TempDirectory, $"mdblist-collage-{boxSet.Id:N}.png");
        try
        {
            _imageEncoder.CreateImageCollage(
                new ImageCollageOptions { InputPaths = posters, OutputPath = output, Width = 600, Height = 900 },
                boxSet.Name);

            await _providerManager.SaveImage(boxSet, output, "image/png", ImageType.Primary, null, null, cancellationToken)
                .ConfigureAwait(false);
            await boxSet.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("MdbListCollections: '{Name}' got a collage poster", boxSet.Name);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private BoxSet? FindCollection(string listRef, string fallbackName)
    {
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true,
            })
            .OfType<BoxSet>()
            .ToList();

        // By name only for a collection we have not stamped yet (made before the id existed).
        return boxSets.FirstOrDefault(b => string.Equals(b.GetProviderId(ListIdKey), listRef, StringComparison.Ordinal))
            ?? boxSets.FirstOrDefault(b => b.GetProviderId(ListIdKey) is null
                && string.Equals(b.Name, fallbackName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every movie and series keyed by provider id. Movie and TV ids live in separate TMDb
    /// namespaces, so the key carries the media kind.
    /// </summary>
    private Dictionary<string, List<Guid>> BuildLibraryIndex()
    {
        var index = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true,
        });

        foreach (var item in items)
        {
            var kind = item is Series ? "show" : "movie";
            foreach (var provider in new[] { MetadataProvider.Imdb, MetadataProvider.Tmdb, MetadataProvider.Tvdb })
            {
                var id = item.GetProviderId(provider);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var key = $"{kind}:{provider}:{id}";
                if (!index.TryGetValue(key, out var list))
                {
                    index[key] = list = new List<Guid>();
                }

                list.Add(item.Id);
            }
        }

        return index;
    }

    private static IEnumerable<string> Keys(MdbListEntry entry)
    {
        var kind = entry.IsShow ? "show" : "movie";
        if (entry.Imdb is not null)
        {
            yield return $"{kind}:{MetadataProvider.Imdb}:{entry.Imdb}";
        }

        if (entry.Tmdb is not null)
        {
            yield return $"{kind}:{MetadataProvider.Tmdb}:{entry.Tmdb}";
        }

        if (entry.Tvdb is not null)
        {
            yield return $"{kind}:{MetadataProvider.Tvdb}:{entry.Tvdb}";
        }
    }
}
