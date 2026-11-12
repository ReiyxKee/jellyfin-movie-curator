using System.Text.Json.Serialization;
using Jellyfin.Plugin.MovieCurator.Curator;
using MediaBrowser.Controller.Library;
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

public class NameRequest
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>Empty/null resets to the automatic "&lt;Original title&gt; (&lt;Year&gt;)".</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>Admin-only API behind the Movie Curator dashboard page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("MovieCurator")]
[Produces("application/json")]
public class CuratorController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly CuratorService _service;

    public CuratorController(ILibraryManager library, ILogger<CuratorController> logger)
    {
        _library = library;
        _service = new CuratorService(library, logger);
    }

    /// <summary>Preview: what would change for every movie. Nothing is touched.</summary>
    [HttpGet("Plan")]
    public ActionResult<List<MoviePlan>> GetPlan([FromQuery] bool rename = true, [FromQuery] bool nfo = true, [FromQuery] bool lockNfo = true)
        => _service.BuildPlan(new PlanOptions { Rename = rename, Nfo = nfo, LockNfo = lockNfo });

    /// <summary>Apply the plan for the selected movies, then start a library scan.</summary>
    [HttpPost("Apply")]
    public async Task<ActionResult<ApplyResult>> Apply([FromBody] ApplyRequest request)
        => await _service.Apply(request.ItemIds, request, HttpContext.RequestAborted).ConfigureAwait(false);

    /// <summary>Reverse the most recent Apply.</summary>
    [HttpPost("Undo")]
    public async Task<ActionResult<ApplyResult>> Undo()
        => await _service.UndoLast(HttpContext.RequestAborted).ConfigureAwait(false);

    /// <summary>Lock or unlock movies so library refreshes / "Refresh all metadata" leave them alone.</summary>
    [HttpPost("Lock")]
    public async Task<ActionResult<int>> Lock([FromBody] LockRequest request, CancellationToken cancellationToken)
        => await _service.SetLocked(request.ItemIds, request.Locked, cancellationToken).ConfigureAwait(false);

    /// <summary>Set (or clear) a custom folder name for one movie.</summary>
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
