using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieCurator.Curator;

/// <summary>A background job the page can list and re-attach to after the tab was closed.</summary>
public interface ICuratorJob
{
    string Id { get; }

    bool Finished { get; }

    DateTime Created { get; }

    JobSummary Summary();
}

/// <summary>What the floating progress bar shows for one job.</summary>
public class JobSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>search | identify | folders</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    /// <summary>movies | series (search / identify jobs)</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("total")] public int Total { get; set; }

    [JsonPropertyName("done")] public int Done { get; set; }

    [JsonPropertyName("failed")] public int Failed { get; set; }

    [JsonPropertyName("finished")] public bool Finished { get; set; }

    [JsonPropertyName("stopMode")] public string? StopMode { get; set; }

    /// <summary>What it's doing right now.</summary>
    [JsonPropertyName("current")] public string? Current { get; set; }

    [JsonPropertyName("canStop")] public bool CanStop { get; set; }

    [JsonPropertyName("created")] public string Created { get; set; } = string.Empty;
}

/// <summary>All curator jobs on this server (running, plus finished ones for a while).</summary>
public static class JobRegistry
{
    private static readonly ConcurrentDictionary<string, ICuratorJob> All = new();

    public static void Add(ICuratorJob job)
    {
        All[job.Id] = job;
        foreach (var old in All.Values.Where(j => j.Finished && j.Created < DateTime.UtcNow.AddHours(-3)).ToList())
        {
            All.TryRemove(old.Id, out _);
        }
    }

    public static List<JobSummary> Summaries() =>
        All.Values.OrderByDescending(j => j.Created).Select(j => j.Summary()).ToList();

    public static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);
}

// ====================================================================== search jobs (batch "Identify selected")

public class SearchJobRequestItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>movie | series</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "movie";

    /// <summary>Search text (title) and optional year / IDs.</summary>
    [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;

    [JsonPropertyName("year")] public int? Year { get; set; }

    [JsonPropertyName("providerIds")] public Dictionary<string, string> ProviderIds { get; set; } = new();

    /// <summary>For showing the row again later: Jellyfin title and folder/file path.</summary>
    [JsonPropertyName("title")] public string? Title { get; set; }

    [JsonPropertyName("path")] public string? Path { get; set; }

    [JsonPropertyName("root")] public string? Root { get; set; }
}

public class SearchJobRequest
{
    [JsonPropertyName("type")] public string Type { get; set; } = "movies";

    [JsonPropertyName("providers")] public List<string> Providers { get; set; } = new();

    [JsonPropertyName("items")] public List<SearchJobRequestItem> Items { get; set; } = new();
}

public class SearchJobItem : SearchJobRequestItem
{
    /// <summary>queued | running | done | stopped</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "queued";

    [JsonPropertyName("current")] public string? Current { get; set; }

    [JsonPropertyName("results")] public List<RemoteSearchResult> Results { get; set; } = new();

    /// <summary>Results per source; -1 = the source failed.</summary>
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts { get; set; } = new();
}

public class SearchJob : ICuratorJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("type")] public string Type { get; set; } = "movies";

    [JsonPropertyName("providers")] public List<string> Providers { get; set; } = new();

    [JsonPropertyName("items")] public List<SearchJobItem> Items { get; set; } = new();

    [JsonPropertyName("finished")] public bool Finished { get; set; }

    [JsonPropertyName("stopMode")] public string? StopMode { get; set; }

    [JsonPropertyName("total")] public int Total => Items.Count;

    [JsonPropertyName("done")] public int Done => Items.Count(i => i.Status is "done" or "stopped");

    [JsonIgnore] public DateTime Created { get; } = DateTime.UtcNow;

    [JsonIgnore] public CancellationTokenSource Cancel { get; } = new();

    public JobSummary Summary()
    {
        var cur = Items.FirstOrDefault(i => i.Status == "running");
        return new JobSummary
        {
            Id = Id,
            Kind = "search",
            Title = $"Searching matches ({Items.Count} {(Type == "series" ? "series" : "movies")})",
            Type = Type,
            Total = Total,
            Done = Done,
            Finished = Finished,
            StopMode = StopMode,
            CanStop = !Finished,
            Current = cur == null ? (Finished ? "done" : "queued") : (cur.Title ?? cur.Query) + " · " + cur.Current,
            Created = JobRegistry.Iso(Created),
        };
    }
}

/// <summary>Searches metadata sources for many items on the server, one item and one source at a time, throttled.</summary>
public static class SearchQueue
{
    private static readonly ConcurrentDictionary<string, SearchJob> Jobs = new();
    private static readonly SemaphoreSlim Gate = new(1, 1); // one search job at a time per server (keeps the throttle honest)

    // minimum gap between requests to the same source (rate-limited sources ban clients that go faster)
    private static readonly Dictionary<string, int> Gaps = new(StringComparer.Ordinal)
    {
        ["AniDB"] = 2500, ["AniSearch"] = 1200, ["AniList"] = 800, ["The Open Movie Database"] = 500, ["TheMovieDb"] = 300,
    };

    private static readonly ConcurrentDictionary<string, DateTime> LastCall = new();

    public static SearchJob Start(SearchJobRequest request, IProviderManager providers, ILogger logger)
    {
        var job = new SearchJob
        {
            Type = request.Type,
            Providers = request.Providers.Count > 0 ? request.Providers : new List<string> { "TheMovieDb" },
            Items = request.Items.Select(i => new SearchJobItem
            {
                ItemId = i.ItemId, Kind = i.Kind, Query = i.Query, Year = i.Year, ProviderIds = i.ProviderIds,
                Title = i.Title, Path = i.Path, Root = i.Root,
            }).ToList(),
        };
        Jobs[job.Id] = job;
        JobRegistry.Add(job);

        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Run(job, providers, logger).ConfigureAwait(false);
            }
            finally
            {
                job.Finished = true;
                Gate.Release();
            }
        });
        return job;
    }

    public static SearchJob? Get(string id) => Jobs.TryGetValue(id, out var j) ? j : null;

    public static SearchJob? Stop(string id, bool force)
    {
        if (Get(id) is not { } job)
        {
            return null;
        }

        job.StopMode = force ? "force" : job.StopMode ?? "stop";
        if (force)
        {
            job.Cancel.Cancel();
        }

        return job;
    }

    private static async Task Run(SearchJob job, IProviderManager providers, ILogger logger)
    {
        var ct = job.Cancel.Token;
        foreach (var it in job.Items)
        {
            if (job.StopMode != null)
            {
                it.Status = "stopped";
                continue;
            }

            it.Status = "running";
            try
            {
                for (var k = 0; k < job.Providers.Count; k++)
                {
                    var provider = job.Providers[k];
                    it.Current = $"{provider} ({k + 1}/{job.Providers.Count})";
                    await Throttle(provider, ct).ConfigureAwait(false);
                    try
                    {
                        var found = (await Search(providers, it, provider, ct).ConfigureAwait(false)).ToList();
                        foreach (var r in found)
                        {
                            r.SearchProviderName ??= provider;
                        }

                        it.Results.AddRange(found);
                        it.Counts[provider] = found.Count;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        it.Counts[provider] = -1;
                        logger.LogWarning(ex, "Curator: search on {Provider} failed for {Query}", provider, it.Query);
                    }
                }

                it.Status = "done";
                it.Current = null;
            }
            catch (OperationCanceledException)
            {
                it.Status = "stopped";
                it.Results.Clear(); // a half-searched row is dropped
                it.Counts.Clear();
            }
        }
    }

    private static Task<IEnumerable<RemoteSearchResult>> Search(IProviderManager providers, SearchJobItem it, string provider, CancellationToken ct)
    {
        Guid.TryParse(it.ItemId, out var itemId);
        if (it.Kind == "series")
        {
            return providers.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = it.Query, Year = it.Year, ProviderIds = new Dictionary<string, string>(it.ProviderIds) },
                    ItemId = itemId,
                    SearchProviderName = provider,
                    IncludeDisabledProviders = true,
                },
                ct);
        }

        return providers.GetRemoteSearchResults<Movie, MovieInfo>(
            new RemoteSearchQuery<MovieInfo>
            {
                SearchInfo = new MovieInfo { Name = it.Query, Year = it.Year, ProviderIds = new Dictionary<string, string>(it.ProviderIds) },
                ItemId = itemId,
                SearchProviderName = provider,
                IncludeDisabledProviders = true,
            },
            ct);
    }

    private static async Task Throttle(string provider, CancellationToken ct)
    {
        var gap = Gaps.TryGetValue(provider, out var g) ? g : 300;
        var last = LastCall.GetValueOrDefault(provider);
        var wait = last.AddMilliseconds(gap) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        LastCall[provider] = DateTime.UtcNow;
    }
}

// ====================================================================== folder jobs (Adjust folders / Undo / Rewind)

public class FolderJob : ICuratorJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    [JsonPropertyName("total")] public int Total { get; set; }

    [JsonPropertyName("done")] public int Done { get; set; }

    [JsonPropertyName("current")] public string? Current { get; set; }

    [JsonPropertyName("finished")] public bool Finished { get; set; }

    [JsonPropertyName("result")] public ApplyResult? Result { get; set; }

    [JsonIgnore] public DateTime Created { get; } = DateTime.UtcNow;

    public JobSummary Summary() => new()
    {
        Id = Id,
        Kind = "folders",
        Title = Title,
        Total = Total,
        Done = Done,
        Failed = Result?.Errors.Count ?? 0,
        Finished = Finished,
        CanStop = false, // folder changes always run to the end; use the history to undo
        Current = Finished ? (Result == null ? "failed" : $"done: {Result.Done} change(s)" + (Result.Errors.Count > 0 ? $", {Result.Errors.Count} problem(s)" : string.Empty)) : Current,
        Created = JobRegistry.Iso(Created),
    };
}

/// <summary>Runs folder changes on the server, one job at a time, reporting progress per change.</summary>
public static class FolderQueue
{
    private static readonly ConcurrentDictionary<string, FolderJob> Jobs = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>work(report) does the changes; report(done, total, text) updates the progress.</summary>
    public static FolderJob Start(string title, Func<Action<int, int, string>, Task<ApplyResult>> work, ILogger logger)
    {
        var job = new FolderJob { Title = title, Current = "queued" };
        Jobs[job.Id] = job;
        JobRegistry.Add(job);
        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                job.Result = await work((done, total, text) =>
                {
                    job.Done = done;
                    job.Total = total;
                    job.Current = text;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                job.Result = new ApplyResult { Errors = { ex.Message } };
                logger.LogError(ex, "Curator: folder job {Title} failed", title);
            }
            finally
            {
                job.Finished = true;
                Gate.Release();
            }
        });
        return job;
    }

    public static FolderJob? Get(string id) => Jobs.TryGetValue(id, out var j) ? j : null;
}
