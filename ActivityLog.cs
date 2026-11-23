using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ReidentifyByTmdb;

/// <summary>
/// A short history of what the plugin did, shown on the settings page.
/// Kept in memory and mirrored to a small text file so it survives a server restart.
/// </summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 500;

    // The file is rewritten from memory once it has grown by this many lines past MaxEntries.
    private const int TrimSlack = 500;

    private readonly ILogger<ActivityLog> _logger;
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private Queue<string>? _entries;
    private int _linesInFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityLog"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ActivityLog(IApplicationPaths applicationPaths, ILogger<ActivityLog> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "ReidentifyByTmdb.log");
    }

    /// <summary>
    /// Adds an entry, stamped with the current local time.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Add(string message)
    {
        // Entries are single lines; item names could in theory contain line breaks.
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + "  "
            + message.ReplaceLineEndings(" ");

        lock (_lock)
        {
            var entries = Load();
            entries.Enqueue(line);
            while (entries.Count > MaxEntries)
            {
                entries.Dequeue();
            }

            // The history is a convenience; never let a disk problem break the actual work.
            try
            {
                if (_linesInFile >= MaxEntries + TrimSlack)
                {
                    File.WriteAllLines(_filePath, entries);
                    _linesInFile = entries.Count;
                }
                else
                {
                    File.AppendAllLines(_filePath, [line]);
                    _linesInFile++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Re-identify: could not write activity log {Path}", _filePath);
            }
        }
    }

    /// <summary>
    /// Gets the most recent entries, newest first.
    /// </summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<string> GetRecent()
    {
        lock (_lock)
        {
            return Load().Reverse().ToArray();
        }
    }

    /// <summary>
    /// Removes all entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Load().Clear();
            _linesInFile = 0;
            try
            {
                File.Delete(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Re-identify: could not delete activity log {Path}", _filePath);
            }
        }
    }

    // Caller holds _lock.
    private Queue<string> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var entries = new Queue<string>();
        try
        {
            if (File.Exists(_filePath))
            {
                foreach (var line in File.ReadLines(_filePath))
                {
                    _linesInFile++;
                    entries.Enqueue(line);
                    if (entries.Count > MaxEntries)
                    {
                        entries.Dequeue();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Re-identify: could not read activity log {Path}", _filePath);
        }

        _entries = entries;
        return entries;
    }
}
