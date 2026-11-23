using System;
using System.Collections.Generic;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ReidentifyByTmdb.Api;

/// <summary>
/// Endpoints used by the plugin's settings page. Administrators only.
/// </summary>
[ApiController]
[Route("ReidentifyByTmdb")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class ReidentifyController : ControllerBase
{
    private readonly ReidentifyService _service;
    private readonly ActivityLog _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReidentifyController"/> class.
    /// </summary>
    /// <param name="service">The re-identify service.</param>
    /// <param name="activity">The activity log.</param>
    public ReidentifyController(ReidentifyService service, ActivityLog activity)
    {
        _service = service;
        _activity = activity;
    }

    /// <summary>
    /// Gets the number of done items and the recent activity, newest first.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ReidentifyStatus> GetStatus()
    {
        var current = _service.Current;
        return new ReidentifyStatus(
            _service.DoneCount,
            current?.Name,
            current is null ? null : (int)(DateTime.UtcNow - current.StartedUtc).TotalSeconds,
            _activity.GetRecent());
    }

    /// <summary>
    /// Forgets all done items, so the next task run redoes the whole library.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("ForgetAll")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ForgetAll()
    {
        _service.ForgetAll();
        return NoContent();
    }

    /// <summary>
    /// Clears the activity log.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("ClearLog")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearLog()
    {
        _activity.Clear();
        return NoContent();
    }
}

/// <summary>
/// What the settings page shows besides the settings themselves.
/// </summary>
/// <param name="DoneCount">The number of items already re-identified.</param>
/// <param name="CurrentItem">The item being worked on right now, or <c>null</c> when idle.</param>
/// <param name="CurrentItemSeconds">How long the current item has been worked on.</param>
/// <param name="Log">Recent activity, newest first.</param>
public record ReidentifyStatus(int DoneCount, string? CurrentItem, int? CurrentItemSeconds, IReadOnlyList<string> Log);
