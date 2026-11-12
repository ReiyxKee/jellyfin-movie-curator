using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieCurator.Curator;

/// <summary>One match to apply: the item and the search result the user picked (cached in the browser, not re-searched).</summary>
public class IdentifyJobRequestItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

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
}

public class IdentifyJobItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("match")] public string? Match { get; set; }

    /// <summary>queued | running | done | failed</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "queued";

    [JsonPropertyName("step")] public string? Step { get; set; }

    [JsonPropertyName("stepIndex")] public int StepIndex { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonIgnore] public RemoteSearchResult Result { get; set; } = new();

    [JsonIgnore] public bool ReplaceImages { get; set; } = true;
}

public class IdentifyJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("items")] public List<IdentifyJobItem> Items { get; set; } = new();

    [JsonPropertyName("total")] public int Total => Items.Count;

    [JsonPropertyName("done")] public int Done => Items.Count(i => i.Status is "done" or "failed");

    [JsonPropertyName("failed")] public int Failed => Items.Count(i => i.Status == "failed");

    [JsonPropertyName("finished")] public bool Finished { get; set; }

    [JsonPropertyName("steps")] public int Steps => 6;

    [JsonIgnore] public DateTime Created { get; } = DateTime.UtcNow;

    [JsonIgnore] public IdentifyJobRequest Options { get; set; } = new();
}

/// <summary>
/// Applies picked matches on the server, one item at a time, so the browser never waits on a long request
/// (Jellyfin's Identify downloads full metadata + images and can take longer than a reverse proxy's timeout).
/// The page polls the job for progress; closing the page doesn't stop it.
/// </summary>
public static class IdentifyQueue
{
    private static readonly ConcurrentDictionary<string, IdentifyJob> Jobs = new();
    private static readonly SemaphoreSlim Gate = new(1, 1); // one job at a time per server

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
            }).ToList(),
        };
        Jobs[job.Id] = job;
        Prune();

        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var it in job.Items)
                {
                    await RunItem(job, it, service, library, providers, fileSystem, logger).ConfigureAwait(false);
                    await Task.Delay(1000).ConfigureAwait(false); // be gentle with the metadata sources
                }
            }
            finally
            {
                job.Finished = true;
                Gate.Release();
                logger.LogInformation("Curator: identify job {Id} finished: {Done} done, {Failed} failed", job.Id, job.Done - job.Failed, job.Failed);
            }
        });

        return job;
    }

    public static IdentifyJob? Get(string id) => Jobs.TryGetValue(id, out var job) ? job : null;

    private static void Prune()
    {
        foreach (var old in Jobs.Values.Where(j => j.Finished && j.Created < DateTime.UtcNow.AddHours(-6)).ToList())
        {
            Jobs.TryRemove(old.Id, out _);
        }
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
        void Step(int i, string label)
        {
            it.StepIndex = i;
            it.Step = label;
        }

        it.Status = "running";
        try
        {
            if (!Guid.TryParse(it.ItemId, out var id) || library.GetItemById(id) is not { } item)
            {
                throw new InvalidOperationException("Item not found (was it moved or removed?)");
            }

            it.Name ??= item.Name;
            var wasLocked = item.IsLocked;

            Step(0, "unlocking");
            if (wasLocked)
            {
                item.IsLocked = false;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }

            // an NFO with the old IDs would pull the item straight back to the old match
            Step(1, "removing old NFO");
            var removed = service.RemoveNfo(item);

            // same as Jellyfin's Identify (ItemLookupController.ApplySearchCriteria)
            Step(2, "applying match (fetching metadata)");
            logger.LogInformation("Curator: applying {Match} to {Name}", it.Match, item.Name);
            item.ProviderIds = it.Result.ProviderIds;
            await providers.RefreshFullItem(
                item,
                new MetadataRefreshOptions(new DirectoryService(fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = true,
                    ReplaceAllImages = it.ReplaceImages,
                    SearchResult = it.Result,
                    RemoveOldMetadata = true,
                },
                CancellationToken.None).ConfigureAwait(false);
            item = library.GetItemById(id) ?? item;

            Step(3, "writing new NFO");
            if (removed > 0 || job.Options.WriteNfo)
            {
                service.WriteNfo(item, job.Options.LockNfo);
            }

            Step(4, "locking");
            if (job.Options.LockAfter || wasLocked)
            {
                item.IsLocked = true;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }

            // a match the user picked is a confirmed one
            Step(5, "marking verified");
            service.SetVerified(new[] { it.ItemId }, true);

            Step(6, "done");
            it.Status = "done";
        }
        catch (Exception ex)
        {
            it.Status = "failed";
            it.Error = ex.Message;
            logger.LogError(ex, "Curator: identify failed for {Name} ({Step})", it.Name, it.Step);
        }
    }
}
