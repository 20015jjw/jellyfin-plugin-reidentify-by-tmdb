using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReidentifyByTmdb;

/// <summary>
/// Re-runs "Identify" on every movie and series that has a TMDB ID and has not been done before.
/// New items are normally handled by <see cref="NewItemListener"/>; this task is for the first
/// pass over an existing library and for catching anything the listener missed.
/// </summary>
public class ReidentifyTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ReidentifyService _service;
    private readonly ActivityLog _activity;
    private readonly ILogger<ReidentifyTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReidentifyTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="service">The re-identify service.</param>
    /// <param name="activity">The activity log.</param>
    /// <param name="logger">The logger.</param>
    public ReidentifyTask(
        ILibraryManager libraryManager,
        ReidentifyService service,
        ActivityLog activity,
        ILogger<ReidentifyTask> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Re-identify media by TMDB ID";

    /// <inheritdoc />
    public string Key => "ReidentifyByTmdbId";

    /// <inheritdoc />
    public string Description =>
        "Re-runs Identify on every movie and series that has not been done yet, using the TMDB ID stored on the item.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            HasTmdbId = true,
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        });

        // One snapshot for the whole run, so saving the settings page mid-run can't change its behaviour.
        var options = ReidentifyOptions.FromCurrentSettings();
        var dryRun = options.DryRun;
        _logger.LogInformation("Re-identify: {Count} movies/series have a TMDB ID", items.Count);
        _activity.Add($"Task started{(dryRun ? " (dry run)" : string.Empty)}: {items.Count} movies/series have a TMDB ID");

        int seen = 0, refreshed = 0, alreadyDone = 0, locked = 0, otherLibrary = 0, wouldDo = 0, failed = 0;
        try
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    switch (await _service.ReidentifyOnceAsync(item, options, cancellationToken).ConfigureAwait(false))
                    {
                        case ReidentifyOutcome.Refreshed:
                            refreshed++;
                            break;
                        case ReidentifyOutcome.AlreadyDone:
                            alreadyDone++;
                            break;
                        case ReidentifyOutcome.DryRun:
                            wouldDo++;
                            break;
                        case ReidentifyOutcome.Locked:
                            locked++;
                            break;
                        case ReidentifyOutcome.NotInSelectedLibrary:
                            otherLibrary++;
                            break;
                        default:
                            // NoTmdbId can't happen here: the query only returns items that have one.
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _service.RecordFailure(item, ex);
                }

                seen++;
                progress.Report(100d * seen / items.Count);
            }
        }
        finally
        {
            var how = cancellationToken.IsCancellationRequested ? "cancelled" : "finished";
            _logger.LogInformation(
                "Re-identify {How}: {Refreshed} refreshed, {WouldDo} dry-run, {AlreadyDone} already done, {Locked} locked, {OtherLibrary} outside selected libraries, {Failed} failed",
                how,
                refreshed,
                wouldDo,
                alreadyDone,
                locked,
                otherLibrary,
                failed);
            _activity.Add(
                $"Task {how}: {refreshed} re-identified, "
                + (dryRun ? $"{wouldDo} would be re-identified, " : string.Empty)
                + $"{alreadyDone} already done, {locked} locked, "
                + (otherLibrary > 0 ? $"{otherLibrary} outside the selected libraries, " : string.Empty)
                + $"{failed} failed");
        }
    }
}
