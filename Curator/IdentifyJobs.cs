using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieCurator.Curator;

/// <summary>One match to apply: the item and the search result the user picked (cached in the browser, not re-searched).</summary>
public class IdentifyJobRequestItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>Its folder / file when the job was started: found again by it if the id changed (moved / renamed).</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("result")] public RemoteSearchResult Result { get; set; } = new();

    [JsonPropertyName("replaceImages")] public bool ReplaceImages { get; set; } = true;
}

public class IdentifyJobRequest
{
    [JsonPropertyName("items")] public List<IdentifyJobRequestItem> Items { get; set; } = new();

    /// <summary>Lock items afterwards (items that were locked before are always re-locked).</summary>
    [JsonPropertyName("lockAfter")] public bool LockAfter { get; set; } = true;

    /// <summary>Write a fresh NFO even if the item had none (one is always written if an old one was removed).</summary>
    [JsonPropertyName("writeNfo")] public bool WriteNfo { get; set; } = true;

    [JsonPropertyName("lockNfo")] public bool LockNfo { get; set; } = true;

    /// <summary>movies | series (for the job list).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "movies";
}

public class IdentifyJobItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("match")] public string? Match { get; set; }

    /// <summary>queued | running | done | failed | cancelled | rolledback</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "queued";

    [JsonPropertyName("step")] public string? Step { get; set; }

    [JsonPropertyName("stepIndex")] public int StepIndex { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonIgnore] public RemoteSearchResult Result { get; set; } = new();

    [JsonIgnore] public bool ReplaceImages { get; set; } = true;

    [JsonIgnore] public string? Path { get; set; }
}

public class IdentifyJob : ICuratorJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("items")] public List<IdentifyJobItem> Items { get; set; } = new();

    [JsonPropertyName("total")] public int Total => Items.Count;

    /// <summary>Items that are finished in any way (done, failed, cancelled, rolled back).</summary>
    [JsonPropertyName("done")] public int Done => Items.Count(i => i.Status is not ("queued" or "running"));

    [JsonPropertyName("failed")] public int Failed => Items.Count(i => i.Status == "failed");

    [JsonPropertyName("cancelled")] public int Cancelled => Items.Count(i => i.Status is "cancelled" or "rolledback");

    [JsonPropertyName("finished")] public bool Finished { get; set; }

    /// <summary>null | "stop" (finish current item, skip the rest) | "force" (abort now, roll back current item)</summary>
    [JsonPropertyName("stopMode")] public string? StopMode { get; set; }

    [JsonPropertyName("steps")] public int Steps => 6;

    [JsonIgnore] public bool Started { get; set; }

    [JsonIgnore] public DateTime Created { get; } = DateTime.UtcNow;

    [JsonIgnore] public IdentifyJobRequest Options { get; set; } = new();

    [JsonIgnore] public CancellationTokenSource Cancel { get; } = new();

    public JobSummary Summary()
    {
        var cur = Items.FirstOrDefault(i => i.Status == "running");
        return new JobSummary
        {
            Id = Id,
            Kind = "identify",
            Title = Items.Count == 1 ? $"Applying {Items[0].Match}" : $"Applying {Items.Count} matches",
            Type = Options.Type,
            Total = Total,
            Done = Done,
            Failed = Failed,
            Finished = Finished,
            StopMode = StopMode,
            CanStop = !Finished,
            Current = cur == null ? (Finished ? "done" : Started ? "next item…" : WorkQueue.Waiting(Id)) : $"{cur.Name} → {cur.Match} · {cur.Step} ({cur.StepIndex}/{Steps})",
            Created = JobRegistry.Iso(Created),
        };
    }
}

/// <summary>
/// Applies picked matches on the server, one item at a time, so the browser never waits on a long request
/// (Jellyfin's Identify downloads full metadata + images and can take longer than a reverse proxy's timeout).
/// The page polls the job for progress; closing the page doesn't stop it.
/// Stop: finish the current item, skip the rest. Force stop: abort now and roll the current item back.
/// </summary>
public static class IdentifyQueue
{
    private static readonly ConcurrentDictionary<string, IdentifyJob> Jobs = new();

    public static IdentifyJob Start(
        IdentifyJobRequest request,
        CuratorService service,
        ILibraryManager library,
        IProviderManager providers,
        IFileSystem fileSystem,
        ILogger logger)
    {
        var job = new IdentifyJob
        {
            Options = request,
            Items = request.Items.Select(i => new IdentifyJobItem
            {
                ItemId = i.ItemId,
                Name = i.Name,
                Match = i.Result.Name + (i.Result.ProductionYear is int y ? $" ({y})" : string.Empty),
                Result = i.Result,
                ReplaceImages = i.ReplaceImages,
                Path = i.Path,
            }).ToList(),
        };
        Jobs[job.Id] = job;
        JobRegistry.Add(job);
        Prune();

        _ = WorkQueue.Enqueue(job.Id, async () =>
        {
            job.Started = true;
            try
            {
                foreach (var it in job.Items)
                {
                    if (job.StopMode != null)
                    {
                        it.Status = "cancelled";
                        it.Step = "not started (stopped)";
                        continue;
                    }

                    await RunItem(job, it, service, library, providers, fileSystem, logger).ConfigureAwait(false);
                    if (job.StopMode == null)
                    {
                        await Task.Delay(1000).ConfigureAwait(false); // be gentle with the metadata sources
                    }
                }
            }
            finally
            {
                job.Finished = true;
                logger.LogInformation(
                    "Curator: identify job {Id} finished: {Done} done, {Failed} failed, {Cancelled} cancelled/rolled back{Stop}",
                    job.Id,
                    job.Items.Count(i => i.Status == "done"),
                    job.Failed,
                    job.Cancelled,
                    job.StopMode == null ? string.Empty : " (stopped: " + job.StopMode + ")");
            }
        });

        return job;
    }

    public static IdentifyJob? Get(string id) => Jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>force=false: finish the current item, skip the rest. force=true: abort now, roll back the current item.</summary>
    public static IdentifyJob? Stop(string id, bool force)
    {
        if (Get(id) is not { } job)
        {
            return null;
        }

        if (force)
        {
            job.StopMode = "force";
            job.Cancel.Cancel();
        }
        else
        {
            job.StopMode ??= "stop";
        }

        return job;
    }

    private static void Prune()
    {
        foreach (var old in Jobs.Values.Where(j => j.Finished && j.Created < DateTime.UtcNow.AddHours(-6)).ToList())
        {
            Jobs.TryRemove(old.Id, out _);
        }
    }

    /// <summary>What an item looked like before we touched it, for a force-stop roll back.</summary>
    private sealed class Snapshot
    {
        public Dictionary<string, string> ProviderIds { get; init; } = new();

        public string? Name { get; init; }

        public string? OriginalTitle { get; init; }

        public int? ProductionYear { get; init; }

        public string? Overview { get; init; }

        public bool IsLocked { get; init; }

        public Dictionary<string, string> Nfos { get; init; } = new();
    }

    private static async Task RunItem(
        IdentifyJob job,
        IdentifyJobItem it,
        CuratorService service,
        ILibraryManager library,
        IProviderManager providers,
        IFileSystem fileSystem,
        ILogger logger)
    {
        var ct = job.Cancel.Token;
        void Step(int i, string label)
        {
            ct.ThrowIfCancellationRequested();
            it.StepIndex = i;
            it.Step = label;
        }

        it.Status = "running";
        Snapshot? before = null;
        Guid id = Guid.Empty;
        try
        {
            // found by id, else by its path (followed through the moves / renames done since the job was started)
            it.Step = "finding the item";
            var found = await service.FindItem(it.ItemId, it.Path, ct).ConfigureAwait(false);
            if (found is not { } item)
            {
                throw new InvalidOperationException("Item not found: it was moved or removed and Jellyfin doesn't list it yet (reload the list and try again)");
            }

            id = item.Id;
            it.ItemId = id.ToString("N");

            it.Name ??= item.Name;
            before = new Snapshot
            {
                ProviderIds = new Dictionary<string, string>(item.ProviderIds),
                Name = item.Name,
                OriginalTitle = item.OriginalTitle,
                ProductionYear = item.ProductionYear,
                Overview = item.Overview,
                IsLocked = item.IsLocked,
                Nfos = service.BackupNfo(item),
            };

            Step(0, "unlocking");
            if (before.IsLocked)
            {
                item.IsLocked = false;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }

            // an NFO with the old IDs would pull the item straight back to the old match
            Step(1, "removing old NFO");
            var removed = service.RemoveNfo(item);

            // only the source you picked: its metadata and artwork are written, then the item is locked before Jellyfin
            // fills in seasons / episodes (so no other source - e.g. AniList ranked first - can replace the title or poster)
            Step(2, "applying match from " + (it.Result.SearchProviderName ?? "the picked source"));
            logger.LogInformation("Curator: applying {Match} to {Name} (only from {Source})", it.Match, item.Name, it.Result.SearchProviderName);
            item.ProviderIds = new Dictionary<string, string>(it.Result.ProviderIds);
            var single = await ApplyFromPickedSource(item, it.Result, providers, library, logger, ct).ConfigureAwait(false);
            if (single)
            {
                item.IsLocked = true;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
            else
            {
                // not what was asked for: say so on the item instead of reporting a clean "done"
                it.Error = $"{it.Result.SearchProviderName ?? "the picked source"} gave no metadata: filled from all enabled sources "
                    + "(the pick's title, year, plot and poster kept). Identify it again to retry.";
            }

            // seasons / episodes (a show) - or everything, when the picked source couldn't be asked on its own
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var progress = item is Series ? WatchEpisodes(it, id, library, watch.Token) : Task.CompletedTask;
            if (!single || item is Series)
            {
                await providers.RefreshFullItem(
                    item,
                    new MetadataRefreshOptions(new DirectoryService(fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ImageRefreshMode = single ? MetadataRefreshMode.Default : MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = !single,
                        ReplaceAllImages = !single && it.ReplaceImages,
                        SearchResult = single ? null : it.Result,
                        RemoveOldMetadata = !single,
                    },
                    ct).ConfigureAwait(false);
            }

            await watch.CancelAsync().ConfigureAwait(false);
            await progress.ConfigureAwait(false);
            item = library.GetItemById(id) ?? item;
            await KeepPickedMatch(item, it.Result, providers, logger, ct).ConfigureAwait(false);

            // the pick is final: always a fresh, locked NFO and a locked item, so no later refresh / source changes it
            Step(3, "writing new NFO");
            service.WriteNfo(item, true);

            Step(4, "locking");
            item.IsLocked = true;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

            // a match the user picked is a confirmed one
            Step(5, "marking verified");
            service.SetVerified(new[] { it.ItemId }, true);

            // Identify only updates the metadata + NFO: files and folders stay where they are. "Rename files" names them
            // from the NFO afterwards (renaming here made Jellyfin drop the show and re-create it as a new item).
            it.StepIndex = 6;
            it.Step = "done";
            it.Status = "done";
        }
        catch (OperationCanceledException) when (job.StopMode == "force")
        {
            await RollBack(it, id, before, service, library, logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            it.Status = "failed";
            it.Error = ex.Message;
            logger.LogError(ex, "Curator: identify failed for {Name} ({Step})", it.Name, it.Step);
        }
    }

    /// <summary>Force stop: put the item back the way it was (IDs, titles, lock, NFO files, verified mark).</summary>
    /// <summary>
    /// Metadata and artwork from only the picked result's source (e.g. TheMovieDb), written onto the item. False when that
    /// source can't be asked on its own (then the normal refresh + "the pick wins" fix-up is used).
    /// </summary>
    private static async Task<bool> ApplyFromPickedSource(BaseItem item, RemoteSearchResult picked, IProviderManager providers, ILibraryManager library, ILogger logger, CancellationToken ct)
    {
        var source = picked.SearchProviderName;
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        try
        {
            BaseItem? fetched = null;
            if (item is Series series)
            {
                var provider = providers.GetMetadataProviders<Series>(item, library.GetLibraryOptions(item))
                    .OfType<IRemoteMetadataProvider<Series, SeriesInfo>>().FirstOrDefault(x => string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase));
                if (provider == null)
                {
                    return false;
                }

                var info = series.GetLookupInfo();
                info.ProviderIds = new Dictionary<string, string>(picked.ProviderIds);
                info.Name = picked.Name;
                info.Year = picked.ProductionYear;
                var r = await provider.GetMetadata(info, ct).ConfigureAwait(false);
                if (r.HasMetadata && r.Item != null)
                {
                    fetched = r.Item;
                    series.Status = r.Item.Status;
                }
            }
            else if (item is MediaBrowser.Controller.Entities.Movies.Movie movie)
            {
                var provider = providers.GetMetadataProviders<MediaBrowser.Controller.Entities.Movies.Movie>(item, library.GetLibraryOptions(item))
                    .OfType<IRemoteMetadataProvider<MediaBrowser.Controller.Entities.Movies.Movie, MovieInfo>>()
                    .FirstOrDefault(x => string.Equals(x.Name, source, StringComparison.OrdinalIgnoreCase));
                if (provider == null)
                {
                    return false;
                }

                var info = movie.GetLookupInfo();
                info.ProviderIds = new Dictionary<string, string>(picked.ProviderIds);
                info.Name = picked.Name;
                info.Year = picked.ProductionYear;
                var r = await provider.GetMetadata(info, ct).ConfigureAwait(false);
                if (r.HasMetadata && r.Item != null)
                {
                    fetched = r.Item;
                }
            }

            if (fetched == null)
            {
                logger.LogWarning("Curator: {Source} returned no metadata for {Name}", source, picked.Name);
                return false;
            }

            item.Name = string.IsNullOrWhiteSpace(fetched.Name) ? picked.Name : fetched.Name;
            item.OriginalTitle = string.IsNullOrWhiteSpace(fetched.OriginalTitle) ? item.Name : fetched.OriginalTitle;
            item.Overview = fetched.Overview ?? picked.Overview;
            item.ProductionYear = fetched.ProductionYear ?? picked.ProductionYear;
            item.PremiereDate = fetched.PremiereDate ?? item.PremiereDate;
            item.EndDate = fetched.EndDate ?? item.EndDate;
            item.CommunityRating = fetched.CommunityRating ?? item.CommunityRating;
            item.OfficialRating = fetched.OfficialRating ?? item.OfficialRating;
            if (fetched.Genres?.Length > 0) item.Genres = fetched.Genres;
            if (fetched.Studios?.Length > 0) item.Studios = fetched.Studios;
            if (fetched.Tags?.Length > 0) item.Tags = fetched.Tags;
            foreach (var kv in fetched.ProviderIds)
            {
                item.ProviderIds[kv.Key] = kv.Value; // ids the picked source knows (e.g. TMDB also gives IMDb / TVDB)
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

            // artwork from the same source only, replacing what's there
            var images = await providers.GetAvailableRemoteImages(item, new RemoteImageQuery(source) { IncludeAllLanguages = false }, ct).ConfigureAwait(false);
            foreach (var type in new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb })
            {
                var best = images.Where(i => i.Type == type).OrderByDescending(i => i.CommunityRating ?? 0).FirstOrDefault();
                if (best?.Url == null)
                {
                    continue;
                }

                try
                {
                    await providers.SaveImage(item, best.Url, type, type == ImageType.Backdrop ? 0 : null, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Curator: could not download the {Type} image from {Source}", type, source);
                }
            }

            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Curator: asking only {Source} failed for {Name}; using the normal refresh", source, picked.Name);
            return false;
        }
    }

    /// <summary>
    /// The match you picked wins: during the refresh every enabled source runs, and one ranked first (e.g. AniList for an
    /// anime library) can find something else by name ("Arcane" -> "1999! Occult..."). Then its ids are dropped and the
    /// picked title / year / description are put back and locked, so the next refresh keeps them too.
    /// </summary>
    private static async Task KeepPickedMatch(BaseItem item, RemoteSearchResult picked, IProviderManager providers, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(picked.Name))
        {
            return;
        }

        // another source took over (its title isn't the pick's): its ids go, and its artwork is replaced below
        // ... or an anime source added itself to a pick from elsewhere (its own match by name: AniList "Arcane" -> "1999!")
        var anime = new[] { "AniList", "AniDB", "AniSearch", "Kitsu" };
        var overridden = !string.Equals(item.Name?.Trim(), picked.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            || (!anime.Any(picked.ProviderIds.ContainsKey) && anime.Any(item.ProviderIds.ContainsKey));
        if (overridden)
        {
            var foreign = item.ProviderIds.Keys.Where(k => !picked.ProviderIds.ContainsKey(k)).ToList();
            logger.LogInformation("Curator: {Source} overrode the picked match {Picked} with {Got}; keeping the pick (dropping {Ids})",
                foreign.Count > 0 ? string.Join("/", foreign) : "another source", picked.Name, item.Name, string.Join(", ", foreign));
            foreach (var k in foreign)
            {
                item.ProviderIds.Remove(k);
            }
        }

        foreach (var kv in picked.ProviderIds)
        {
            item.ProviderIds[kv.Key] = kv.Value;
        }

        item.Name = picked.Name;
        if (overridden || string.IsNullOrWhiteSpace(item.OriginalTitle))
        {
            item.OriginalTitle = picked.Name; // otherwise the source's (native) original title stays
        }
        if (picked.ProductionYear is int y)
        {
            item.ProductionYear = y;
        }

        if (!string.IsNullOrWhiteSpace(picked.Overview))
        {
            item.Overview = picked.Overview;
        }

        var locked = item.LockedFields.ToList();
        foreach (var f in new[] { MetadataField.Name, MetadataField.Overview })
        {
            if (!locked.Contains(f))
            {
                locked.Add(f);
            }
        }

        item.LockedFields = locked.ToArray();
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

        // the poster is the pick's too: a source ranked first for images (AniList...) can bring another show's poster
        // even when the title stayed right, so the picked result's poster always replaces it
        if (!string.IsNullOrWhiteSpace(picked.ImageUrl))
        {
            try
            {
                await providers.SaveImage(item, picked.ImageUrl, ImageType.Primary, null, ct).ConfigureAwait(false);
                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Curator: could not download the picked poster for {Name}", picked.Name);
            }
        }
    }

    /// <summary>While Jellyfin refreshes a show, counts the episodes it has refreshed since the step started.</summary>
    private static async Task WatchEpisodes(IdentifyJobItem it, Guid seriesId, ILibraryManager library, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        try
        {
            var episodes = library.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { seriesId },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
            }).Select(e => e.Id).ToList();
            if (episodes.Count == 0)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var done = episodes.Count(e => library.GetItemById(e) is { } ep && ep.DateLastRefreshed >= start);
                it.Step = done == 0
                    ? $"applying match (fetching show metadata, then {episodes.Count} episodes)"
                    : $"applying match (episodes {done} / {episodes.Count})";
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // refresh finished (or the job was stopped)
        }
        catch (Exception)
        {
            // progress is only a nicety: never fail the identify over it
        }
    }

    private static async Task RollBack(IdentifyJobItem it, Guid id, Snapshot? before, CuratorService service, ILibraryManager library, ILogger logger)
    {
        it.Step = "rolling back";
        try
        {
            if (before == null || library.GetItemById(id) is not { } item)
            {
                it.Status = "rolledback";
                it.Step = "stopped before any change";
                return;
            }

            // the verified mark (if already set) belongs to the new IDs, so clear it before restoring them
            if (it.StepIndex >= 5)
            {
                service.SetVerified(new[] { it.ItemId }, false);
            }

            item.ProviderIds = new Dictionary<string, string>(before.ProviderIds);
            item.Name = before.Name;
            item.OriginalTitle = before.OriginalTitle;
            item.ProductionYear = before.ProductionYear;
            item.Overview = before.Overview;
            item.IsLocked = before.IsLocked;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

            service.RestoreNfo(item, before.Nfos);

            it.Status = "rolledback";
            it.Step = "rolled back" + (it.StepIndex >= 2 ? " (downloaded images can't be restored)" : string.Empty);
            logger.LogInformation("Curator: force stop, rolled back {Name}", before.Name);
        }
        catch (Exception ex)
        {
            it.Status = "failed";
            it.Error = "Roll back failed: " + ex.Message;
            logger.LogError(ex, "Curator: roll back failed for {Name}", it.Name);
        }
    }
}
