using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ReidentifyByTmdb.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReidentifyByTmdb;

/// <summary>
/// What happened when an item was offered for re-identification.
/// </summary>
public enum ReidentifyOutcome
{
    /// <summary>The item was re-identified and recorded as done.</summary>
    Refreshed,

    /// <summary>The item was re-identified on an earlier occasion; nothing was done.</summary>
    AlreadyDone,

    /// <summary>The item has no TMDB ID (yet); nothing was done and it is not recorded.</summary>
    NoTmdbId,

    /// <summary>The item is locked; nothing was done and it is not recorded.</summary>
    Locked,

    /// <summary>The item is outside the libraries selected in the settings; nothing was done.</summary>
    NotInSelectedLibrary,

    /// <summary>Dry-run mode: the item would have been re-identified; nothing was done.</summary>
    DryRun
}

/// <summary>
/// The settings as they were at one moment, so that a whole run behaves consistently
/// even if the settings page is saved while it is in progress.
/// </summary>
/// <param name="ReplaceImages">Whether existing images are replaced.</param>
/// <param name="DryRun">Whether to only log what would be done.</param>
/// <param name="LibraryIds">The libraries to process; empty means all.</param>
public sealed record ReidentifyOptions(bool ReplaceImages, bool DryRun, IReadOnlySet<Guid> LibraryIds)
{
    /// <summary>
    /// Captures the current settings.
    /// </summary>
    /// <returns>The options.</returns>
    public static ReidentifyOptions FromCurrentSettings()
    {
        var settings = ReidentifyService.Settings;
        var libraries = new HashSet<Guid>();
        foreach (var id in settings.LibraryIds ?? [])
        {
            if (Guid.TryParse(id, out var guid))
            {
                libraries.Add(guid);
            }
        }

        return new ReidentifyOptions(settings.ReplaceImages, settings.DryRun, libraries);
    }
}

/// <summary>
/// Re-runs "Identify" on a movie or series using only its TMDB ID, and remembers which items
/// have been done so that each one is only ever processed once.
/// </summary>
/// <remarks>
/// The list of done items is a plain text file next to the plugin configuration files: one
/// item ID per line, and "-ID" for an item that was later removed from the library. Deleting
/// the file makes everything eligible again.
/// </remarks>
public sealed class ReidentifyService
{
    // Must match TmdbUtils.ProviderName in the server so the TMDB provider runs first.
    private const string TmdbProviderName = "TheMovieDb";

    private static readonly string _tmdbKey = MetadataProvider.Tmdb.ToString();

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ActivityLog _activity;
    private readonly ILogger<ReidentifyService> _logger;
    private readonly string _doneFilePath;
    private readonly Lock _doneLock = new();

    // Never disposed on purpose: a task run can still be inside ReidentifyOnceAsync at shutdown,
    // and a SemaphoreSlim whose wait handle is never used holds nothing that needs disposing.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private HashSet<Guid>? _done;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReidentifyService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="activity">The activity log.</param>
    /// <param name="logger">The logger.</param>
    public ReidentifyService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        ActivityLog activity,
        ILogger<ReidentifyService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _activity = activity;
        _logger = logger;
        _doneFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "ReidentifyByTmdb.done.txt");
    }

    /// <summary>
    /// Gets the current settings.
    /// </summary>
    public static PluginConfiguration Settings => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets the number of items recorded as done.
    /// </summary>
    public int DoneCount
    {
        get
        {
            lock (_doneLock)
            {
                return LoadDone().Count;
            }
        }
    }

    /// <summary>
    /// Gets whether the item was already re-identified.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns><c>true</c> if the item is recorded as done.</returns>
    public bool IsDone(Guid itemId)
    {
        lock (_doneLock)
        {
            return LoadDone().Contains(itemId);
        }
    }

    /// <summary>
    /// Forgets an item, so that an item later re-added at the same path is processed again.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    public void Forget(Guid itemId)
    {
        lock (_doneLock)
        {
            if (LoadDone().Remove(itemId))
            {
                // Appending a removal line keeps this cheap when a whole library is removed;
                // the file is compacted the next time it is loaded.
                TryAppend("-" + FormatId(itemId));
            }
        }
    }

    /// <summary>
    /// Forgets every item, so that the next run of the task re-identifies the whole library again.
    /// </summary>
    /// <returns>The number of items forgotten.</returns>
    public int ForgetAll()
    {
        int count;
        lock (_doneLock)
        {
            var done = LoadDone();
            count = done.Count;

            // Delete first: if that fails, memory and disk still agree.
            File.Delete(_doneFilePath);
            done.Clear();
        }

        _logger.LogInformation("Re-identify: forgot all {Count} done items", count);
        _activity.Add($"Forgot all {count} done items; the next task run will redo the whole library");
        return count;
    }

    /// <summary>
    /// Re-identifies the item by its TMDB ID unless that was already done.
    /// </summary>
    /// <param name="item">A movie or series.</param>
    /// <param name="options">The settings to use.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="InvalidOperationException">TMDB appears to have returned nothing for the item.</exception>
    public async Task<ReidentifyOutcome> ReidentifyOnceAsync(
        BaseItem item,
        ReidentifyOptions options,
        CancellationToken cancellationToken)
    {
        // One at a time, so the scheduled task and the new-item listener can't both take the same item.
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDone(item.Id))
            {
                return ReidentifyOutcome.AlreadyDone;
            }

            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return ReidentifyOutcome.NoTmdbId;
            }

            if (!IsInSelectedLibrary(item, options))
            {
                return ReidentifyOutcome.NotInSelectedLibrary;
            }

            if (item.IsLocked)
            {
                // The refresh pipeline ignores locked items; don't wipe their other IDs for nothing.
                _logger.LogInformation("Re-identify: skipping locked item {Name} ({Path})", item.Name, item.Path);
                _activity.Add($"Skipped (locked): \"{item.Name}\"");
                return ReidentifyOutcome.Locked;
            }

            if (options.DryRun)
            {
                _logger.LogInformation("Re-identify (dry run): would re-identify {Name} (tmdb {TmdbId})", item.Name, tmdbId);
                _activity.Add($"Dry run, would re-identify: \"{item.Name}\" (tmdb {tmdbId})");
                return ReidentifyOutcome.DryRun;
            }

            var oldName = item.Name;
            var oldIds = new Dictionary<string, string>(
                item.ProviderIds ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            // Only the TMDB ID is passed on; every other ID (IMDb, TVDB, ...) is rebuilt from TMDB.
            var searchResult = new RemoteSearchResult
            {
                Name = item.Name,
                ProductionYear = item.ProductionYear,
                SearchProviderName = TmdbProviderName
            };
            searchResult.ProviderIds[_tmdbKey] = tmdbId;

            // Mirrors ItemLookupController.ApplySearchCriteria.
            item.SetProviderIds(searchResult.ProviderIds);
            await _providerManager.RefreshFullItem(
                item,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = true,
                    ReplaceAllImages = options.ReplaceImages,
                    SearchResult = searchResult,
                    RemoveOldMetadata = true
                },
                cancellationToken).ConfigureAwait(false);

            var refreshed = _libraryManager.GetItemById(item.Id) ?? item;

            // The server logs and swallows provider errors, so a refresh that "succeeded" may have
            // fetched nothing. The tell-tale: the item had IMDb/TVDB IDs before, and none came back.
            // Put the old IDs back and leave the item not-done so a later task run retries it.
            if (HasOtherIds(oldIds) && !HasOtherIds(refreshed.ProviderIds))
            {
                refreshed.ProviderIds = oldIds;
                await refreshed.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "TMDB seems to have returned nothing (the item's other provider IDs did not come back). "
                    + "Its old IDs were restored; it will be retried on the next task run.");
            }

            MarkDone(item.Id);
            _logger.LogInformation("Re-identify: tmdb {TmdbId}: \"{OldName}\" -> \"{NewName}\"", tmdbId, oldName, refreshed.Name);
            _activity.Add($"Re-identified (tmdb {tmdbId}): \"{oldName}\" -> \"{refreshed.Name}\"");
            return ReidentifyOutcome.Refreshed;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Records a failure in the server log and the activity log.
    /// </summary>
    /// <param name="item">The item that failed.</param>
    /// <param name="exception">The error.</param>
    public void RecordFailure(BaseItem item, Exception exception)
    {
        _logger.LogError(exception, "Re-identify: failed for {Name} ({Path})", item.Name, item.Path);
        _activity.Add($"FAILED: \"{item.Name}\": {exception.Message}");
    }

    private static string FormatId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static bool HasOtherIds(IReadOnlyDictionary<string, string>? providerIds)
    {
        return providerIds is not null
            && providerIds.Any(p => !string.Equals(p.Key, _tmdbKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(p.Value));
    }

    private static bool HasOtherIds(Dictionary<string, string>? providerIds)
        => HasOtherIds((IReadOnlyDictionary<string, string>?)providerIds);

    private bool IsInSelectedLibrary(BaseItem item, ReidentifyOptions options)
    {
        if (options.LibraryIds.Count == 0)
        {
            return true;
        }

        foreach (var folder in _libraryManager.GetCollectionFolders(item))
        {
            if (options.LibraryIds.Contains(folder.Id))
            {
                return true;
            }
        }

        return false;
    }

    private void MarkDone(Guid itemId)
    {
        lock (_doneLock)
        {
            // The item was refreshed whether or not the file write works, so it counts as done
            // in memory either way; a failed write only means it is redone after a restart.
            if (LoadDone().Add(itemId))
            {
                TryAppend(FormatId(itemId));
            }
        }
    }

    // Caller holds _doneLock.
    private void TryAppend(string line)
    {
        try
        {
            File.AppendAllLines(_doneFilePath, [line]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Re-identify: could not update the done list {Path}", _doneFilePath);
            _activity.Add($"WARNING: could not update the done list ({ex.Message}); items may be redone after a restart");
        }
    }

    // Caller holds _doneLock.
    private HashSet<Guid> LoadDone()
    {
        if (_done is not null)
        {
            return _done;
        }

        var done = new HashSet<Guid>();
        var hasRemovals = false;
        if (File.Exists(_doneFilePath))
        {
            foreach (var raw in File.ReadLines(_doneFilePath))
            {
                var line = raw.Trim();
                if (line.StartsWith('-'))
                {
                    hasRemovals = true;
                    if (Guid.TryParse(line.AsSpan(1), out var removed))
                    {
                        done.Remove(removed);
                    }
                }
                else if (Guid.TryParse(line, out var id))
                {
                    done.Add(id);
                }
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_doneFilePath)!);
        }

        if (hasRemovals)
        {
            // Compact: write the surviving IDs to a temporary file and swap it in, so a crash
            // mid-write can't leave a truncated list.
            try
            {
                var temp = _doneFilePath + ".tmp";
                File.WriteAllLines(temp, done.Select(FormatId));
                File.Move(temp, _doneFilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Re-identify: could not compact the done list {Path}", _doneFilePath);
            }
        }

        _logger.LogInformation("Re-identify: {Count} items already done ({Path})", done.Count, _doneFilePath);
        _done = done;
        return done;
    }
}
