using System.Text.Json.Serialization;
using Jellyfin.Plugin.MovieCurator.Curator;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieCurator.Api;

public class ApplyRequest : PlanOptions
{
    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();
}

public class LockRequest
{
    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();

    [JsonPropertyName("locked")] public bool Locked { get; set; } = true;
}

public class VerifyRequest
{
    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();

    [JsonPropertyName("verified")] public bool Verified { get; set; } = true;
}

public class NfoRequest
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("lockNfo")] public bool LockNfo { get; set; } = true;
}

public class GroupRequest
{
    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();

    /// <summary>Combine: the folder to keep (optional).</summary>
    [JsonPropertyName("mainItemId")] public string? MainItemId { get; set; }
}

public class NameRequest
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>Empty/null resets to the automatic "&lt;Original title&gt; (&lt;Year&gt;)".</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public class SeasonRequest
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>Season for episodes Jellyfin couldn't place in a season; null resets to automatic.</summary>
    [JsonPropertyName("season")] public int? Season { get; set; }
}

/// <summary>Admin-only API behind the Movie Curator dashboard page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MovieCurator")]
[Produces("application/json")]
public class CuratorController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<CuratorController> _logger;
    private readonly CuratorService _service;

    public CuratorController(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger<CuratorController> logger)
    {
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
        _service = new CuratorService(library, logger);
    }

    /// <summary>
    /// Apply picked matches in the background (unlock, remove old NFO, apply, new NFO, lock, verify per item).
    /// Returns at once; poll GET IdentifyJobs/{id}. Avoids reverse-proxy timeouts on slow metadata downloads.
    /// </summary>
    [HttpPost("IdentifyJobs")]
    public ActionResult<IdentifyJob> StartIdentifyJob([FromBody] IdentifyJobRequest request)
    {
        if (request.Items.Count == 0)
        {
            return BadRequest("No items.");
        }

        return IdentifyQueue.Start(request, _service, _library, _providers, _fileSystem, _logger);
    }

    /// <summary>Stop a job: force=false finishes the current item and skips the rest; force=true aborts now and rolls back the current item.</summary>
    [HttpPost("IdentifyJobs/{id}/Stop")]
    public ActionResult<IdentifyJob> StopIdentifyJob([FromRoute] string id, [FromQuery] bool force = false) =>
        IdentifyQueue.Stop(id, force) is { } job ? job : NotFound();

    /// <summary>Progress of an identify job.</summary>
    [HttpGet("IdentifyJobs/{id}")]
    public ActionResult<IdentifyJob> GetIdentifyJob([FromRoute] string id) =>
        IdentifyQueue.Get(id) is { } job ? job : NotFound();

    /// <summary>Preview: what would change for every movie (type=movies) or series (type=series). Nothing is touched.</summary>
    [HttpGet("Plan")]
    public ActionResult<List<ItemPlan>> GetPlan(
        [FromQuery] string type = "movies",
        [FromQuery] bool rename = true,
        [FromQuery] bool episodes = false,
        [FromQuery] bool nfo = true,
        [FromQuery] bool lockNfo = true,
        [FromQuery] bool flatten = true)
        => _service.BuildPlan(new PlanOptions { Type = type, Rename = rename, Episodes = episodes, Nfo = nfo, LockNfo = lockNfo, Flatten = flatten });

    /// <summary>Apply the plan for the selected movies, then start a library scan.</summary>
    [HttpPost("Apply")]
    public async Task<ActionResult<ApplyResult>> Apply([FromBody] ApplyRequest request)
        => await _service.Apply(request.ItemIds, request, HttpContext.RequestAborted).ConfigureAwait(false);

    /// <summary>Reverse the most recent Apply.</summary>
    [HttpPost("Undo")]
    public async Task<ActionResult<ApplyResult>> Undo()
        => await _service.UndoLast(HttpContext.RequestAborted).ConfigureAwait(false);

    /// <summary>Lock or unlock movies/series (a series includes its seasons and episodes) so library refreshes / "Refresh all metadata" leave them alone.</summary>
    [HttpPost("Lock")]
    public async Task<ActionResult<int>> Lock([FromBody] LockRequest request, CancellationToken cancellationToken)
        => await _service.SetLocked(request.ItemIds, request.Locked, cancellationToken).ConfigureAwait(false);

    /// <summary>Remove an item's NFO (before re-identifying, so the old IDs in it can't override the new match).</summary>
    [HttpPost("RemoveNfo")]
    public ActionResult<int> RemoveNfo([FromBody] NfoRequest request)
        => Guid.TryParse(request.ItemId, out var id) && _library.GetItemById(id) is { } item ? _service.RemoveNfo(item) : NotFound();

    /// <summary>Write a fresh NFO from the item's current metadata (after re-identifying).</summary>
    [HttpPost("WriteNfo")]
    public ActionResult<string> WriteNfo([FromBody] NfoRequest request)
        => Guid.TryParse(request.ItemId, out var id) && _library.GetItemById(id) is { } item ? _service.WriteNfo(item, request.LockNfo) : NotFound();

    /// <summary>Combine series folders into one show (as seasons), whatever their IDs.</summary>
    [HttpPost("Group/Combine")]
    public ActionResult<int> CombineSeries([FromBody] GroupRequest request) => _service.CombineSeries(request.ItemIds, request.MainItemId);

    /// <summary>Detach one series folder from its group.</summary>
    [HttpPost("Group/Detach")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DetachSeries([FromBody] GroupRequest request)
    {
        foreach (var id in request.ItemIds)
        {
            _service.DetachSeries(id);
        }

        return NoContent();
    }

    /// <summary>Back to automatic grouping for these series folders.</summary>
    [HttpPost("Group/Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ResetSeriesGrouping([FromBody] GroupRequest request)
    {
        _service.ResetSeriesGrouping(request.ItemIds);
        return NoContent();
    }

    /// <summary>Mark the current identification of items as correct ("Looks right"), or unmark it.</summary>
    [HttpPost("Verify")]
    public ActionResult<int> Verify([FromBody] VerifyRequest request) => _service.SetVerified(request.ItemIds, request.Verified);

    /// <summary>Set (or clear) the season used for a series' episodes that have no season in Jellyfin.</summary>
    [HttpPost("Season")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetSeason([FromBody] SeasonRequest request)
    {
        if (!Guid.TryParse(request.ItemId, out var id) || _library.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        _service.SetSeasonOverride(item, request.Season);
        return NoContent();
    }

    /// <summary>Set (or clear) a custom folder name for one movie or series.</summary>
    [HttpPost("Name")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetName([FromBody] NameRequest request)
    {
        if (!Guid.TryParse(request.ItemId, out var id) || _library.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        _service.SetOverride(item, request.Name);
        return NoContent();
    }
}
