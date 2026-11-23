using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReidentifyByTmdb;

/// <summary>
/// Re-identifies each movie and series once, shortly after it is added to the library.
/// </summary>
/// <remarks>
/// New items are queued when the library reports them and processed after the library scan
/// that found them has finished, so the scan's own metadata pass can't overwrite the result.
/// Items missed here (for example because the server restarted mid-scan) are picked up by
/// the manual scheduled task, which shares the same list of done items.
/// </remarks>
public sealed class NewItemListener : IHostedService, IDisposable
{
    // How long to wait after the last new item (and for the scan to end) before processing a batch.
    private static readonly TimeSpan _settleDelay = TimeSpan.FromSeconds(30);

    // An item with no TMDB ID yet is looked at again this many times, this far apart.
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(2);
    private const int MaxAttempts = 4;

    private readonly ILibraryManager _libraryManager;
    private readonly ReidentifyService _service;
    private readonly ActivityLog _activity;
    private readonly ILogger<NewItemListener> _logger;
    private readonly Channel<(Guid ItemId, int Attempt)> _queue = Channel.CreateUnbounded<(Guid ItemId, int Attempt)>();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewItemListener"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="service">The re-identify service.</param>
    /// <param name="activity">The activity log.</param>
    /// <param name="logger">The logger.</param>
    public NewItemListener(
        ILibraryManager libraryManager,
        ReidentifyService service,
        ActivityLog activity,
        ILogger<NewItemListener> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _worker = Task.Run(() => ProcessQueueAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series && !e.Item.IsVirtualItem && ReidentifyService.Settings.EnableOnNewItems)
        {
            _queue.Writer.TryWrite((e.Item.Id, 1));
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            try
            {
                _service.Forget(e.Item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-identify: could not forget removed item {Name}", e.Item.Name);
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Wait once per batch, not once per item: let the scan that added the items
                // finish its own metadata pass first.
                do
                {
                    await Task.Delay(_settleDelay, cancellationToken).ConfigureAwait(false);
                }
                while (_libraryManager.IsScanRunning);

                var batch = new List<(Guid ItemId, int Attempt)>();
                while (_queue.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                var options = ReidentifyOptions.FromCurrentSettings();
                foreach (var (itemId, attempt) in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessOneAsync(itemId, attempt, options, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task ProcessOneAsync(Guid itemId, int attempt, ReidentifyOptions options, CancellationToken cancellationToken)
    {
        // Fetch the item again: the TMDB ID may have been set after it was first reported.
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        try
        {
            var outcome = await _service.ReidentifyOnceAsync(item, options, cancellationToken).ConfigureAwait(false);
            if (outcome != ReidentifyOutcome.NoTmdbId)
            {
                return;
            }

            if (attempt < MaxAttempts)
            {
                // Its metadata may simply not have been fetched yet; look again later.
                _ = RequeueLaterAsync(itemId, attempt + 1, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Re-identify: new item {Name} still has no TMDB ID; left alone", item.Name);
                _activity.Add($"New item left alone, no TMDB ID: \"{item.Name}\"");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _service.RecordFailure(item, ex);
        }
    }

    private async Task RequeueLaterAsync(Guid itemId, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            _queue.Writer.TryWrite((itemId, attempt));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
