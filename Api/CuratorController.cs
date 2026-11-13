using System.Text.Json.Serialization;
using Jellyfin.Plugin.MovieCurator.Curator;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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

public class FolderRuleRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>season | show | movies | leave</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "season";

    [JsonPropertyName("season")] public int? Season { get; set; }

    /// <summary>A video's episode number (set by hand).</summary>
    [JsonPropertyName("episode")] public int? Episode { get; set; }

    /// <summary>"into": the show folder to move it into.</summary>
    [JsonPropertyName("target")] public string? Target { get; set; }
}

public class SeasonNameRequest
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>Empty = back to the metadata source's title.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public class CollectionRemoveRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();
}

public class CollectionRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new();
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
    private readonly ICollectionManager _collections;
    private readonly ILogger<CuratorController> _logger;
    private readonly CuratorService _service;

    public CuratorController(ILibraryManager library, ILibraryMonitor monitor, IProviderManager providers, IFileSystem fileSystem, ICollectionManager collections, ILogger<CuratorController> logger)
    {
        _collections = collections;
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
        _service = new CuratorService(library, monitor, logger);
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

    // ------------------------------------------------------------------ background jobs (survive closing the page)
    /// <summary>All curator jobs: running ones and those finished in the last few hours.</summary>
    [HttpGet("Jobs")]
    public ActionResult<List<JobSummary>> Jobs() => JobRegistry.Summaries();

    /// <summary>Search metadata sources for many items on the server (batch "Identify selected").</summary>
    [HttpPost("SearchJobs")]
    public ActionResult<SearchJob> StartSearchJob([FromBody] SearchJobRequest request)
        => request.Items.Count == 0 ? BadRequest("No items.") : SearchQueue.Start(request, _providers, _logger);

    [HttpGet("SearchJobs/{id}")]
    public ActionResult<SearchJob> GetSearchJob([FromRoute] string id) => SearchQueue.Get(id) is { } job ? job : NotFound();

    [HttpPost("SearchJobs/{id}/Stop")]
    public ActionResult<SearchJob> StopSearchJob([FromRoute] string id, [FromQuery] bool force = false)
        => SearchQueue.Stop(id, force) is { } job ? job : NotFound();

    /// <summary>Apply folder changes in the background.</summary>
    [HttpPost("FolderJobs/Apply")]
    public ActionResult<FolderJob> StartApplyJob([FromBody] ApplyRequest request)
    {
        var count = request.ItemIds.Count;
        var title = $"{(request.MoveUp ? "Moving up" : request.Mode == "move" ? "Moving folders" : request.Mode == "rename" ? "Renaming files" : "Adjusting folders")} ({count} {(request.IsSeries ? "series" : count == 1 ? "movie" : "movies")})";
        return FolderQueue.Start(title, report => _service.Apply(request.ItemIds, request, CancellationToken.None, report), _logger);
    }

    /// <summary>Undo the last folder change in the background.</summary>
    [HttpPost("FolderJobs/Undo")]
    public ActionResult<FolderJob> StartUndoJob()
        => FolderQueue.Start("Undoing the last folder change", report => _service.UndoLast(CancellationToken.None, report), _logger);

    /// <summary>Rewind to before a history entry, in the background.</summary>
    [HttpPost("FolderJobs/Rewind/{id}")]
    public ActionResult<FolderJob> StartRewindJob([FromRoute] string id)
        => FolderQueue.Start("Rewinding folder changes", report => _service.Rewind(id, CancellationToken.None, report), _logger);

    [HttpGet("FolderJobs/{id}")]
    public ActionResult<FolderJob> GetFolderJob([FromRoute] string id) => FolderQueue.Get(id) is { } job ? job : NotFound();

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

    /// <summary>Folder-change history, newest first.</summary>
    [HttpGet("History")]
    public ActionResult<List<HistoryEntry>> History() => _service.History();

    /// <summary>Rewind to just before a history entry: undo it and every applied change after it.</summary>
    [HttpPost("History/{id}/Rewind")]
    public async Task<ActionResult<ApplyResult>> Rewind([FromRoute] string id)
        => await _service.Rewind(id, HttpContext.RequestAborted).ConfigureAwait(false);

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

    /// <summary>
    /// Group shows (e.g. the seasons of one franchise in the flat layout) or movies into a Jellyfin collection.
    /// An existing collection with that name is reused. Returns the collection's id.
    /// </summary>
    [HttpPost("Collections/Remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> RemoveFromCollection([FromBody] CollectionRemoveRequest request)
    {
        var ids = request.ItemIds.Select(i => Guid.TryParse(i, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        var box = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.BoxSet }, Recursive = true })
            .OfType<BoxSet>()
            .FirstOrDefault(b => string.Equals(b.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (box == null || ids.Count == 0)
        {
            return NotFound();
        }

        await _collections.RemoveFromCollectionAsync(box.Id, ids).ConfigureAwait(false);
        _logger.LogInformation("Curator: removed {Count} item(s) from collection {Name}", ids.Count, box.Name);
        if (_library.GetItemById(box.Id) is BoxSet left && !left.GetLinkedChildren().Any())
        {
            // nothing left in it: the collection goes too
            _library.DeleteItem(left, new DeleteOptions { DeleteFileLocation = true });
            _logger.LogInformation("Curator: deleted the now empty collection {Name}", box.Name);
        }

        return NoContent();
    }

    [HttpPost("Collections")]
    public async Task<ActionResult<string>> AddToCollection([FromBody] CollectionRequest request)
    {
        var name = request.Name.Trim();
        var ids = request.ItemIds.Select(i => Guid.TryParse(i, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        if (name.Length == 0 || ids.Count == 0)
        {
            return BadRequest("A name and at least one item are needed.");
        }

        var existing = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.BoxSet }, Recursive = true })
            .OfType<BoxSet>()
            .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            await _collections.AddToCollectionAsync(existing.Id, ids).ConfigureAwait(false);
            _logger.LogInformation("Curator: added {Count} item(s) to collection {Name}", ids.Count, name);
            return existing.Id.ToString("N");
        }

        var box = await _collections.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = name,
            ItemIdList = ids.Select(g => g.ToString("N")).ToArray(),
            IsLocked = false,
        }).ConfigureAwait(false);
        _logger.LogInformation("Curator: created collection {Name} with {Count} item(s)", name, ids.Count);
        return box.Id.ToString("N");
    }

    /// <summary>Library scan state: changes waiting for a scan, scan running.</summary>
    [HttpGet("Scan")]
    public ActionResult<ScanStatus> GetScan() => CuratorService.GetScanStatus();

    /// <summary>Tell Jellyfin about the waiting folder changes; with full=true, scan all libraries.</summary>
    [HttpPost("Scan")]
    public ActionResult<ScanStatus> StartScan([FromQuery] bool full = false)
    {
        _service.StartScan(full);
        return CuratorService.GetScanStatus();
    }

    /// <summary>Set a season's title (locked), or clear it to get the metadata source's title back.</summary>
    [HttpPost("SeasonName")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SetSeasonName([FromBody] SeasonNameRequest request)
        => await _service.SetSeasonName(request.ItemId, request.Name, HttpContext.RequestAborted).ConfigureAwait(false) ? NoContent() : NotFound();

    /// <summary>Set what happens to a subfolder (season number / separate show) or a loose video (Movies library).</summary>
    [HttpPost("FolderRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetFolderRule([FromBody] FolderRuleRequest request)
        => _service.SetFolderRule(request.Path, request.Action, request.Season, request.Episode, request.Target)
            ? NoContent() : BadRequest("Path is not inside a library (or the 'group with' target is invalid).");

    /// <summary>A folder's subfolders and videos ("📂 Open" in the folder tree).</summary>
    [HttpGet("Folder")]
    public ActionResult<List<FolderEntry>> ListFolder([FromQuery] string path) => _service.ListFolder(path);

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
