using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MovieCurator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieCurator.Curator;

public class PlanOptions
{
    /// <summary>"movies" or "series".</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "movies";

    [JsonPropertyName("rename")] public bool Rename { get; set; } = true;

    /// <summary>Series only: move/rename episodes into season folders.</summary>
    [JsonPropertyName("episodes")] public bool Episodes { get; set; }

    [JsonPropertyName("nfo")] public bool Nfo { get; set; } = true;

    [JsonPropertyName("lockNfo")] public bool LockNfo { get; set; } = true;

    /// <summary>
    /// Move items out of collection / container folders to the library root: movies in "Rush Hour 1-3/" and
    /// shows inside "Fate Series/" each get their own top-level folder.
    /// </summary>
    [JsonPropertyName("flatten")] public bool Flatten { get; set; } = true;

    public bool IsSeries => string.Equals(Type, "series", StringComparison.OrdinalIgnoreCase);
}

public class PlanOp
{
    /// <summary>mkdir | move | dirmove | rmdir | nfo | display</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;

    [JsonPropertyName("note")] public string? Note { get; set; }

    [JsonPropertyName("itemId")] public string? ItemId { get; set; }

    [JsonIgnore] public string? Content { get; set; }
}

/// <summary>Preview of what would change for one movie or one series.</summary>
public class ItemPlan
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "movie"; // movie | series

    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>Movie: video file. Series: series folder.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>Library folder it lives in, so the UI can show short relative paths.</summary>
    [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;

    [JsonPropertyName("jellyfinName")] public string JellyfinName { get; set; } = string.Empty;

    [JsonPropertyName("originalTitle")] public string? OriginalTitle { get; set; }

    [JsonPropertyName("year")] public int? Year { get; set; }

    /// <summary>Plot / description from Jellyfin.</summary>
    [JsonPropertyName("overview")] public string? Overview { get; set; }

    [JsonPropertyName("providerIds")] public Dictionary<string, string> ProviderIds { get; set; } = new();

    /// <summary>Folder name.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;

    /// <summary>New Jellyfin display title from the display template (null = unchanged).</summary>
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }

    [JsonPropertyName("titleSource")] public string TitleSource { get; set; } = "metadata"; // metadata | custom | filename

    [JsonPropertyName("layout")] public string Layout { get; set; } = string.Empty; // loose | single | shared | series

    [JsonPropertyName("suspicious")] public bool Suspicious { get; set; }

    /// <summary>The user confirmed this identification ("Looks right"); it's no longer flagged as suspicious.</summary>
    [JsonPropertyName("verified")] public bool Verified { get; set; }

    /// <summary>Jellyfin's "Lock this item" flag: refreshes won't change its metadata.</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; }

    /// <summary>Title/year guessed from the file or folder name, used for "Suggest matches".</summary>
    [JsonPropertyName("guessName")] public string GuessName { get; set; } = string.Empty;

    [JsonPropertyName("guessYear")] public int? GuessYear { get; set; }

    [JsonPropertyName("finalPath")] public string FinalPath { get; set; } = string.Empty;

    /// <summary>Series: episode files found / episodes that can't be renamed (no season or episode number).</summary>
    [JsonPropertyName("episodeCount")] public int EpisodeCount { get; set; }

    [JsonPropertyName("episodesSkipped")] public int EpisodesSkipped { get; set; }

    /// <summary>Series: the season number used in episode names, and where it came from (custom | jellyfin | name | default).</summary>
    [JsonPropertyName("season")] public int? Season { get; set; }

    /// <summary>Series: episode files Jellyfin has no season number for (they go to <see cref="Season"/>).</summary>
    [JsonPropertyName("unplacedEpisodes")] public int UnplacedEpisodes { get; set; }

    [JsonPropertyName("seasonSource")] public string? SeasonSource { get; set; }

    /// <summary>Series: Jellyfin's seasons (of every folder in a group), in season order.</summary>
    [JsonPropertyName("seasons")] public List<SeasonCard> Seasons { get; set; } = new();

    /// <summary>Series: its season subfolders, with the season number each one becomes.</summary>
    [JsonPropertyName("seasonFolders")] public List<SeasonFolderInfo> SeasonFolders { get; set; } = new();

    /// <summary>Series: unnumbered videos directly in the series folder (movies / specials).</summary>
    [JsonPropertyName("looseVideos")] public List<LooseVideoInfo> LooseVideos { get; set; } = new();

    /// <summary>Series: every folder in this group ("auto" = same IDs, "manual" = combined by you); empty when alone.</summary>
    [JsonPropertyName("members")] public List<GroupMember> Members { get; set; } = new();

    [JsonPropertyName("groupKind")] public string? GroupKind { get; set; }

    /// <summary>Series: you detached this folder from its automatic group.</summary>
    [JsonPropertyName("detached")] public bool Detached { get; set; }

    /// <summary>Series: this folder only holds other shows (e.g. "Fate Series/Fate Zero/…"); their folder names.</summary>
    [JsonPropertyName("containerShows")] public List<string> ContainerShows { get; set; } = new();

    /// <summary>Series: other folders Jellyfin identified as the same show; their episodes merge into this one.</summary>
    [JsonPropertyName("mergedFolders")] public List<string> MergedFolders { get; set; } = new();

    /// <summary>Series: Jellyfin's season/episode numbering is inconsistent, so episodes are left alone.</summary>
    [JsonPropertyName("seasonConflict")] public bool SeasonConflict { get; set; }

    [JsonPropertyName("ops")] public List<PlanOp> Ops { get; set; } = new();

    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

/// <summary>One folder inside a group of series folders shown as one show.</summary>
public class GroupMember
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    [JsonPropertyName("jellyfinName")] public string JellyfinName { get; set; } = string.Empty;

    [JsonPropertyName("originalTitle")] public string? OriginalTitle { get; set; }

    [JsonPropertyName("year")] public int? Year { get; set; }

    [JsonPropertyName("providerIds")] public Dictionary<string, string> ProviderIds { get; set; } = new();

    [JsonPropertyName("episodeCount")] public int EpisodeCount { get; set; }

    /// <summary>Season this folder's episodes become (from its name, a value you set, or Jellyfin).</summary>
    [JsonPropertyName("season")] public int? Season { get; set; }

    [JsonPropertyName("isMain")] public bool IsMain { get; set; }

    [JsonPropertyName("locked")] public bool Locked { get; set; }

    [JsonPropertyName("guessName")] public string GuessName { get; set; } = string.Empty;

    [JsonPropertyName("guessYear")] public int? GuessYear { get; set; }

    [JsonPropertyName("kind")] public string Kind { get; set; } = "series";
}

/// <summary>A Jellyfin season of a show: number, title, artwork, episode count.</summary>
public class SeasonCard
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    /// <summary>The show (folder) this season belongs to (grouped shows have several).</summary>
    [JsonPropertyName("seriesItemId")] public string SeriesItemId { get; set; } = string.Empty;

    [JsonPropertyName("index")] public int? Index { get; set; }

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("overview")] public string? Overview { get; set; }

    [JsonPropertyName("episodeCount")] public int EpisodeCount { get; set; }

    /// <summary>Season folder on disk (empty when Jellyfin made the season from episode numbers only).</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    [JsonPropertyName("hasImage")] public bool HasImage { get; set; }

    /// <summary>The title was set by hand (Name field locked).</summary>
    [JsonPropertyName("nameLocked")] public bool NameLocked { get; set; }
}

/// <summary>A subfolder of a series folder (usually one season).</summary>
public class SeasonFolderInfo
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    [JsonPropertyName("videoCount")] public int VideoCount { get; set; }

    /// <summary>Season it becomes (null = unknown, please set one).</summary>
    [JsonPropertyName("season")] public int? Season { get; set; }

    /// <summary>custom | jellyfin | name | unset</summary>
    [JsonPropertyName("seasonSource")] public string SeasonSource { get; set; } = "unset";

    /// <summary>season | show</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "season";
}

/// <summary>A video directly in a series folder that Jellyfin couldn't number (often a movie or special).</summary>
public class LooseVideoInfo
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>leave | movies</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "leave";
}

/// <summary>One folder change ("Adjust folders" run) in the undo history.</summary>
public class HistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>Local time of the run (ISO, no zone).</summary>
    [JsonPropertyName("time")] public string Time { get; set; } = string.Empty;

    [JsonPropertyName("undone")] public bool Undone { get; set; }

    [JsonPropertyName("total")] public int Total { get; set; }

    [JsonPropertyName("kinds")] public Dictionary<string, int> Kinds { get; set; } = new();

    [JsonPropertyName("items")] public List<string> Items { get; set; } = new();

    [JsonPropertyName("ops")] public List<PlanOp> Ops { get; set; } = new();
}

/// <summary>Library scan state for the "Scan library" button.</summary>
public class ScanStatus
{
    /// <summary>Folder changes made since the last scan (when scanning after each change is off).</summary>
    [JsonPropertyName("pending")] public int Pending { get; set; }

    [JsonPropertyName("scanning")] public bool Scanning { get; set; }

    /// <summary>Scan automatically after each folder change.</summary>
    [JsonPropertyName("auto")] public bool Auto { get; set; }

    [JsonPropertyName("lastScan")] public string? LastScan { get; set; }
}

public class ApplyResult
{
    [JsonPropertyName("done")] public int Done { get; set; }

    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();

    [JsonPropertyName("log")] public string? Log { get; set; }
}

/// <summary>Plans and applies a tidy folder layout for movies and series, inside Jellyfin.</summary>
public class CuratorService
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".flv", ".mpg", ".mpeg" };

    private static readonly HashSet<string> SubExt = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup" };

    private static readonly HashSet<string> SidecarExt = new(SubExt, StringComparer.OrdinalIgnoreCase)
        { ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".tbn" };

    /// <summary>For movies, a match from these is usually wrong (anime plugins grabbing live-action films).</summary>
    private static readonly string[] AnimeProviders = { "AniList", "AniDB", "AniSearch" };

    private static readonly string[] SeriesIdProviders = { "Tmdb", "Tvdb", "Imdb", "AniList", "AniDB", "AniSearch" };

    private static readonly Regex IdTag = new(@"\[(imdbid|tmdbid|tvdbid|anidbid)-[^\]]+\]", RegexOptions.IgnoreCase);

    private static readonly Regex Brackets = new(
        @"\[[^\]]*\]|【[^】]*】|\((?:\d{3,4}p|4k|x26[45]|hevc|bd|web(?:-?dl|rip)?|bluray)[^)]*\)",
        RegexOptions.IgnoreCase);

    private static readonly (string Pattern, string Suffix)[] LangHints =
    {
        ("cht|big5|繁|traditional|zh-?tw|zh-?hant", ".zh-TW"),
        ("chs|gb|简|simplified|chinese|chi|zh|中文", ".zh"),
        ("eng|english|en", ".en"),
        ("jpn|japanese|ja|jp", ".ja"),
        ("nl|dut|dutch", ".nl"),
    };

    private static readonly Dictionary<char, char> Illegal = new()
    {
        [':'] = '：', ['/'] = '／', ['\\'] = '＼', ['?'] = '？', ['*'] = '＊',
        ['"'] = '”', ['<'] = '＜', ['>'] = '＞', ['|'] = '｜',
    };

    public const string DefaultMovieFolder = "{originalTitle} ({year})";
    /// <summary>Default series layout: "Series/Season 1 - Season title/1：Series title - 01.mkv".</summary>
    public const string DefaultSeriesFolder = "{originalTitle}";
    public const string DefaultSeasonFolder = "Season {season} - {seasonTitle}";
    public const string DefaultEpisodeFile = "{season}:{title} - {episode:00}";

    private readonly ILibraryManager _library;
    private readonly ILogger _logger;

    public CuratorService(ILibraryManager library, ILogger logger)
    {
        _library = library;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string LogDir => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "logs");

    // ------------------------------------------------------------------ name helpers
    private static string CleanSpaces(string s)
    {
        s = s.Contains(' ') ? Regex.Replace(s, @"\s+", " ") : Regex.Replace(s, @"[ _.]+", " ");
        return Regex.Replace(s.Trim(), @"(\s+-+)+$", string.Empty).Trim(); // keeps "-Take On Me-"
    }

    private static string CleanName(string name)
    {
        var c = CleanSpaces(Brackets.Replace(name, " "));
        return c.Length > 0 ? c : name;
    }

    public static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Illegal.TryGetValue(ch, out var r) ? r : ch);
        }

        return sb.ToString().Trim().TrimEnd('.');
    }

    /// <summary>"Deadpool.2016.720p.BluRay.x264-[YTS.AG]" -> ("Deadpool", 2016).</summary>
    public static (string Name, int? Year) GuessFromName(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var clean = Regex.Replace(Brackets.Replace(IdTag.Replace(c, string.Empty), string.Empty), @"[._\s]+", " ").Trim();
            var m = Regex.Match(clean, @"^(.+?)[\s(]+((?:19|20)\d\d)(?!\d)");
            if (m.Success && m.Groups[1].Value.Trim().Length > 0)
            {
                return (CleanSpaces(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            }
        }

        return (CleanName(candidates[0]), null);
    }

    private static string BaseKey(string name) => CleanSpaces(Brackets.Replace(name, " ")).ToLowerInvariant();

    private static string LangSuffix(string name)
    {
        var low = name.ToLowerInvariant();
        foreach (var (pattern, suffix) in LangHints)
        {
            if (Regex.IsMatch(low, $"(^|[^a-z])({pattern})([^a-z]|$)"))
            {
                return suffix;
            }
        }

        return string.Empty;
    }

    private static bool IsJunk(string path)
    {
        var n = System.IO.Path.GetFileName(path);
        return n.StartsWith('.') || path.Contains("__MACOSX", StringComparison.Ordinal);
    }

    private static bool IsVideo(string f) => VideoExt.Contains(System.IO.Path.GetExtension(f)) && !IsJunk(f);

    private static bool HasVideosBelow(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(IsVideo);

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static List<string> Sidecars(string video, IEnumerable<string> siblings)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(video);
        return siblings.Where(f =>
        {
            var n = System.IO.Path.GetFileName(f);
            return f != video && SidecarExt.Contains(System.IO.Path.GetExtension(f))
                && n.StartsWith(stem, StringComparison.Ordinal) && n.Length > stem.Length
                && (n[stem.Length] == '.' || n[stem.Length] == '-');
        }).ToList();
    }

    /// <summary>Subfolder names that mean "part of this show" rather than "another show".</summary>
    private static readonly Regex SeasonLike = new(
        @"^(season|series|s|staffel|saison|temporada)\s*\d+\b|^\d+(st|nd|rd|th)\s+season\b|^specials?$|^第\s*\S+\s*[期季]$|^\d{1,2}$"
        + @"|^(extras?|bonus|ova|ovas|oad|nced|ncop|nc|menu|bd extras|bluray extras|featurettes|trailers|other|subs?|subtitles)$",
        RegexOptions.IgnoreCase);

    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
        { "series", "collection", "movies", "movie", "the", "season", "complete", "saga", "franchise" };

    /// <summary>
    /// A container folder holds no videos itself, only 2+ subfolders with videos whose names look like separate
    /// shows (not "Season 1", "S2", "Specials", "OVA"…). Returns those subfolders, or null.
    /// </summary>
    private static int VideoCount(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count(IsVideo);

    /// <summary>Each season is its own show (default) instead of season folders inside one show.</summary>
    private static bool Flat => !string.Equals(Config.SeriesLayout, "nested", StringComparison.OrdinalIgnoreCase);

    /// <summary>Subfolders that belong to a show but are never a show of their own.</summary>
    private static readonly Regex ExtrasLike = new(
        @"^(extras?|bonus|nced|ncop|nc|menu|bd extras|bluray extras|featurettes|trailers|other|subs?|subtitles|scans|cds?)$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Subfolders to move out as separate shows, or null. Nested layout: a folder with no videos of its own whose 2+
    /// subfolders look like separate shows (not "Season 1", "S2", "Specials"…). Flat layout: any 2+ video subfolders
    /// (seasons included) except extras, since every season is its own show.
    /// </summary>
    private static List<string>? ContainerChildren(string dir)
    {
        var kids = Directory.GetDirectories(dir)
            .Where(d => !IsJunk(d) && HasVideosBelow(d) && !ExtrasLike.IsMatch(System.IO.Path.GetFileName(d).Trim()))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        if (kids.Count < 2)
        {
            return null;
        }

        if (Flat)
        {
            // a season / show folder holds several episodes; one-video folders (e.g. "S01 E01/") are episode folders
            var shows = kids.Where(k => VideoCount(k) >= 2).ToList();
            return shows.Count >= 2 ? shows : null;
        }

        if (Directory.EnumerateFiles(dir).Any(IsVideo) || kids.Any(k => SeasonLike.IsMatch(System.IO.Path.GetFileName(k).Trim())))
        {
            return null;
        }

        return kids;
    }

    private static IEnumerable<string> Words(string s) =>
        Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0 && !GenericWords.Contains(w));

    private static bool SharesWord(string a, string b)
    {
        var wa = Words(a).ToList();
        var wb = Words(b).ToList();
        return wa.Any(x => wb.Contains(x) || (x.Length >= 3 && wb.Any(y => y.Length >= 3 && (y.Contains(x, StringComparison.Ordinal) || x.Contains(y, StringComparison.Ordinal)))));
    }

    /// <summary>"Fate Series" + "1. Fate Zero [BD]" -> "Fate Zero"; "Date A Live" + "Movie" -> "Date A Live - Movie".</summary>
    private static string FlattenName(string container, string child)
    {
        var c = CleanName(Regex.Replace(child, @"^\s*\d{1,2}\s*[.)\-]\s*", string.Empty));
        c = Regex.Replace(c, @"_+", " ").Trim(' ', '-', '.'); // "[Hi10]_Highschool_DxD_BorN_[BD]" -> "Highschool DxD BorN"
        c = Regex.Replace(c, @"\s{2,}", " ");
        if (c.Length == 0)
        {
            c = child;
        }

        if (!SharesWord(container, c))
        {
            c = CleanName(container) + " - " + c;
        }

        return Safe(c);
    }

    private static Dictionary<string, string> Ids(BaseItem item) =>
        item.ProviderIds.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Keys for custom names. Series keys are prefixed: TMDB movie and TV IDs are different number spaces.</summary>
    private static IEnumerable<string> OverrideKeys(BaseItem item)
    {
        var prefix = item is Series ? "tv-" : string.Empty;
        foreach (var provider in new[] { "Tmdb", "Imdb", "Tvdb" })
        {
            if (item.ProviderIds.TryGetValue(provider, out var v) && !string.IsNullOrEmpty(v))
            {
                yield return prefix + provider.ToLowerInvariant() + ":" + v;
            }
        }

        yield return "path:" + item.Path;
    }

    // ------------------------------------------------------------------ "looks right" marks
    /// <summary>Identity of the current match: type + provider IDs (or the path when there are none).</summary>
    private static string Signature(BaseItem item)
    {
        var ids = item.ProviderIds
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && kv.Key != "TmdbCollection")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value)
            .ToList();
        return (item is Series ? "series|" : "movie|") + (ids.Count > 0 ? string.Join("|", ids) : "path=" + item.Path);
    }

    private static bool IsVerified(BaseItem item) => Config.Verified.Contains(Signature(item));

    /// <summary>Mark (or unmark) the current identification of these items as correct.</summary>
    public int SetVerified(IEnumerable<string> itemIds, bool verified)
    {
        var n = 0;
        foreach (var id in itemIds)
        {
            if (!Guid.TryParse(id, out var guid) || _library.GetItemById(guid) is not { } item)
            {
                continue;
            }

            var sig = Signature(item);
            Config.Verified.RemoveAll(v => v == sig);
            if (verified)
            {
                Config.Verified.Add(sig);
            }

            n++;
        }

        Plugin.Instance!.SaveConfiguration();
        return n;
    }

    // ------------------------------------------------------------------ templates
    /// <summary>Values available to movie and series templates.</summary>
    private static Dictionary<string, object?> ItemTokens(BaseItem item) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = item.Name ?? string.Empty,
        ["originalTitle"] = string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.Name ?? string.Empty : item.OriginalTitle,
        ["year"] = item.ProductionYear,
        ["tmdb"] = item.ProviderIds.GetValueOrDefault("Tmdb"),
        ["imdb"] = item.ProviderIds.GetValueOrDefault("Imdb"),
        ["tvdb"] = item.ProviderIds.GetValueOrDefault("Tvdb"),
    };

    /// <summary>
    /// Fill a template. "{name}" inserts a value, "{name:00}" formats a number (zero-padded to 2 digits).
    /// Unknown tokens are left as typed so mistakes are visible in the preview.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, object?> values)
    {
        var s = Regex.Replace(template, @"\{(\w+)(?::([^}]*))?\}", m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var v))
            {
                return m.Value;
            }

            var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
            return v switch
            {
                null => string.Empty,
                int i when fmt != null => i.ToString(fmt, CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(fmt, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? string.Empty,
            };
        });
        return ApplyRules(TidyEmpty(s));
    }

    /// <summary>Remove leftovers of missing values: "()", "[]", "[imdbid-]", dangling " - ".</summary>
    private static string TidyEmpty(string s)
    {
        s = Regex.Replace(s, @"\(\s*\)|\[\s*\]|\[(imdbid|tmdbid|tvdbid)-\]", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ");
        s = Regex.Replace(s, @"^[\s\-–—_.]+|[\s\-–—_]+$", string.Empty);
        return s.Trim();
    }

    /// <summary>User regex find/replace rules, in order. Invalid or slow patterns are skipped.</summary>
    public static string ApplyRules(string s)
    {
        foreach (var rule in Config.CleanupRules.Where(r => !string.IsNullOrEmpty(r.Pattern)))
        {
            try
            {
                s = Regex.Replace(s, rule.Pattern, rule.Replacement ?? string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // broken rule: skip it (the settings page validates patterns before saving)
            }
        }

        return s.Trim();
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed record Names(string Folder, string File, string? Display, string Source);

    private static string? CustomName(BaseItem item)
    {
        foreach (var key in OverrideKeys(item))
        {
            var o = Config.Overrides.FirstOrDefault(x => x.Key == key);
            if (o != null && !string.IsNullOrWhiteSpace(o.Name))
            {
                return Safe(o.Name.Trim());
            }
        }

        return null;
    }

    private static string? DisplayFor(BaseItem item, string template)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        var d = Render(template, ItemTokens(item));
        return d.Length > 0 ? d : null;
    }

    private static Names MovieNames(Movie item, string fallback)
    {
        var fromFile = Safe(ApplyRules(CleanName(IdTag.Replace(fallback, " "))));
        var display = DisplayFor(item, Config.DisplayTemplate);
        if (CustomName(item) is { } custom)
        {
            return new Names(custom, custom, display, "custom");
        }

        var identified = !string.IsNullOrWhiteSpace(item.Name)
            && (item.ProviderIds.ContainsKey("Tmdb") || item.ProviderIds.ContainsKey("Imdb"));
        if (!identified)
        {
            return new Names(fromFile, fromFile, null, "filename");
        }

        var tokens = ItemTokens(item);
        var folder = Safe(Render(Or(Config.FolderTemplate, DefaultMovieFolder), tokens));
        folder = folder.Length > 0 ? folder : fromFile;
        var file = string.IsNullOrWhiteSpace(Config.FileTemplate) ? folder : Safe(Render(Config.FileTemplate, tokens));
        return new Names(folder, file.Length > 0 ? file : folder, display, "metadata");
    }

    private static Names SeriesNames(Series item)
    {
        var fromFolder = Safe(ApplyRules(CleanName(IdTag.Replace(System.IO.Path.GetFileName(item.Path), " "))));
        var display = DisplayFor(item, Config.SeriesDisplayTemplate);
        if (CustomName(item) is { } custom)
        {
            return new Names(custom, custom, display, "custom");
        }

        if (string.IsNullOrWhiteSpace(item.Name) || !SeriesIdProviders.Any(item.ProviderIds.ContainsKey))
        {
            return new Names(fromFolder, fromFolder, null, "filename");
        }

        var folder = Safe(Render(Or(Config.SeriesFolderTemplate, DefaultSeriesFolder), ItemTokens(item)));
        folder = folder.Length > 0 ? folder : fromFolder;
        return new Names(folder, folder, display, "metadata");
    }

    private static readonly Dictionary<string, int> RomanSeasons = new(StringComparer.Ordinal)
        { ["II"] = 2, ["III"] = 3, ["IV"] = 4, ["V"] = 5, ["VI"] = 6, ["VII"] = 7, ["VIII"] = 8 };

    private static readonly Dictionary<char, int> CjkDigits = new()
        { ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10 };

    /// <summary>Season number written in a title: "Season 2", "S2", "2nd Season", "第2期", "第二季", "Title II".</summary>
    public static int? SeasonFromName(params string?[] names)
    {
        foreach (var n in names.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            Match m;
            if ((m = Regex.Match(n!, @"\bseason\s*(\d{1,2})\b", RegexOptions.IgnoreCase)).Success
                || (m = Regex.Match(n!, @"\b(\d{1,2})(?:st|nd|rd|th)\s+season\b", RegexOptions.IgnoreCase)).Success
                || (m = Regex.Match(n!, @"(?<![A-Za-z])S(\d{1,2})(?![\dA-Za-z])")).Success
                || (m = Regex.Match(n!, @"第\s*(\d{1,2})\s*[期季]")).Success)
            {
                return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            if ((m = Regex.Match(n!, @"第([一二三四五六七八九十])[期季]")).Success)
            {
                return CjkDigits[m.Groups[1].Value[0]];
            }

            if ((m = Regex.Match(n!, @"\s(II|III|IV|V|VI|VII|VIII)(?=$|[\s:：\-])")).Success)
            {
                return RomanSeasons[m.Groups[1].Value];
            }
        }

        return null;
    }

    private static int? CustomSeason(BaseItem item)
    {
        foreach (var key in OverrideKeys(item))
        {
            var o = Config.Overrides.FirstOrDefault(x => x.Key == "season|" + key);
            if (o != null && int.TryParse(o.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>Set (or clear with null) the season number used for a series' episode names.</summary>
    public void SetSeasonOverride(BaseItem item, int? season)
    {
        var keys = OverrideKeys(item).Select(k => "season|" + k).ToList();
        Config.Overrides.RemoveAll(o => keys.Contains(o.Key));
        if (season is int n)
        {
            Config.Overrides.AddRange(keys.Select(k => new NameOverride { Key = k, Name = n.ToString(CultureInfo.InvariantCulture) }));
        }

        Plugin.Instance!.SaveConfiguration();
    }

    public void SetOverride(BaseItem item, string? name)
    {
        var keys = OverrideKeys(item).ToList();
        Config.Overrides.RemoveAll(o => keys.Contains(o.Key));
        if (!string.IsNullOrWhiteSpace(name))
        {
            Config.Overrides.AddRange(keys.Select(k => new NameOverride { Key = k, Name = name.Trim() }));
        }

        Plugin.Instance!.SaveConfiguration();
    }

    /// <summary>
    /// Season of an episode. Jellyfin's number is trusted when it's 0 (specials) or 2+. When it's 1 or missing,
    /// anime split into one folder per season usually reports "1" for every folder, so the folder's own hint wins:
    /// a season you set for that folder, else a season written in its name ("Season 2", "2nd Season", "第2期"),
    /// else Jellyfin's 1, else 1.
    /// </summary>
    private static (int Season, string Source) SeasonFor(Episode e, int? custom, int? fromName)
    {
        if (e.ParentIndexNumber is int pn && pn != 1)
        {
            return (pn, "jellyfin");
        }

        if (custom is int c)
        {
            return (c, "custom");
        }

        if (fromName is int n)
        {
            return (n, "name");
        }

        return e.ParentIndexNumber is int one ? (one, "jellyfin") : (1, "default");
    }

    /// <summary>Season title from Jellyfin (e.g. from TMDB/TVDB); empty when it's just "Season 2" / "Specials".</summary>
    private static string SeasonTitle(Episode e)
    {
        var name = e.Season?.Name?.Trim() ?? string.Empty;
        return Regex.IsMatch(name, @"^(season\s*\d+|specials?|シーズン\s*\d+|第\s*\d+\s*[季期])$", RegexOptions.IgnoreCase)
            ? string.Empty
            : name;
    }

    // ------------------------------------------------------------------ planning (shared)
    private sealed class Ctx
    {
        public HashSet<string> Taken { get; } = new(StringComparer.Ordinal);

        public bool IsTaken(string p) => Taken.Contains(p) || File.Exists(p) || Directory.Exists(p);
    }

    private HashSet<string> LibraryRoots() => _library.GetVirtualFolders()
        .SelectMany(f => f.Locations)
        .Select(l => l.TrimEnd('/'))
        .ToHashSet(StringComparer.Ordinal);

    private static string RootOf(string dir, IEnumerable<string> roots) =>
        roots.Where(r => dir == r || IsUnder(dir, r)).OrderByDescending(r => r.Length).FirstOrDefault() ?? string.Empty;

    private static bool Move(ItemPlan p, Ctx ctx, string src, string dst, string kind, string? note = null)
    {
        if (src == dst)
        {
            return true;
        }

        var caseOnly = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase);
        if (ctx.IsTaken(dst) && !caseOnly)
        {
            p.Warnings.Add($"Skipped, target already exists: {dst}");
            return false;
        }

        ctx.Taken.Add(dst);
        p.Ops.Add(new PlanOp { Kind = kind, From = src, To = dst, Note = note });
        return true;
    }

    private static void MkDir(ItemPlan p, Ctx ctx, string dir)
    {
        if (!Directory.Exists(dir) && ctx.Taken.Add(dir))
        {
            p.Ops.Add(new PlanOp { Kind = "mkdir", To = dir });
        }
    }

    private static void AddDisplayOp(ItemPlan p, BaseItem item, string? display)
    {
        if (display != null && display != item.Name)
        {
            p.DisplayName = display;
            p.Ops.Add(new PlanOp { Kind = "display", From = item.Name ?? string.Empty, To = display, ItemId = p.ItemId });
        }
    }

    public List<ItemPlan> BuildPlan(PlanOptions opt) => opt.IsSeries ? BuildSeriesPlan(opt) : BuildMoviePlan(opt);

    // ------------------------------------------------------------------ movies
    private List<Movie> Movies() =>
        _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Movie>()
            .Where(m => !string.IsNullOrEmpty(m.Path) && File.Exists(m.Path))
            .OrderBy(m => m.Path, StringComparer.Ordinal)
            .ToList();

    private List<ItemPlan> BuildMoviePlan(PlanOptions opt)
    {
        var roots = LibraryRoots();
        var ctx = new Ctx();
        var plans = new List<ItemPlan>();

        foreach (var group in Movies().GroupBy(m => System.IO.Path.GetDirectoryName(m.Path)!))
        {
            var dir = group.Key;
            var siblings = Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal).ToList();
            string layout;
            if (roots.Contains(dir))
            {
                layout = "loose";
            }
            else if (siblings.Count(IsVideo) == 1 && !Directory.GetDirectories(dir).Where(d => !IsJunk(d)).Any(HasVideosBelow))
            {
                layout = "single";
            }
            else
            {
                layout = "shared"; // several movies in one folder -> each gets its own subfolder
            }

            var root = RootOf(dir, roots);
            foreach (var movie in group)
            {
                try
                {
                    var plan = PlanMovie(movie, dir, siblings, layout, opt, ctx, root);
                    plan.Root = root;
                    plans.Add(plan);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Curator: could not plan {Path}", movie.Path);
                }
            }
        }

        return plans;
    }

    private static ItemPlan PlanMovie(Movie m, string dir, List<string> siblings, string layout, PlanOptions opt, Ctx ctx, string root)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(m.Path);
        var names = MovieNames(m, layout == "single" ? System.IO.Path.GetFileName(dir) : stem);
        var title = names.Folder;
        var ids = Ids(m);
        var p = new ItemPlan
        {
            Kind = "movie",
            ItemId = m.Id.ToString("N"),
            Path = m.Path,
            JellyfinName = m.Name,
            OriginalTitle = m.OriginalTitle,
            Year = m.ProductionYear,
            Overview = m.Overview,
            ProviderIds = ids,
            Title = title,
            FileName = names.File,
            TitleSource = names.Source,
            Layout = layout,
            Verified = IsVerified(m),
            Suspicious = !IsVerified(m) && (!(ids.ContainsKey("Tmdb") || ids.ContainsKey("Imdb")) || AnimeProviders.Any(ids.ContainsKey)),
            Locked = m.IsLocked,
        };
        (p.GuessName, p.GuessYear) = layout == "single"
            ? GuessFromName(stem, System.IO.Path.GetFileName(dir))
            : GuessFromName(stem);

        // 0. display title shown in Jellyfin
        AddDisplayOp(p, m, names.Display);

        // 1. where the movie's folder ends up
        string target, final;
        var flattenFrom = (string?)null; // collection folder this movie leaves when flattening
        if (layout == "single")
        {
            target = dir;
            var parent = System.IO.Path.GetDirectoryName(dir)!;
            var newParent = opt.Flatten && root.Length > 0 && parent != root ? root : parent;
            flattenFrom = newParent != parent ? parent : null;
            final = opt.Rename || flattenFrom != null
                ? System.IO.Path.Combine(newParent, opt.Rename ? title : System.IO.Path.GetFileName(dir))
                : dir;
        }
        else
        {
            var baseDir = opt.Flatten && layout == "shared" && root.Length > 0 ? root : dir;
            flattenFrom = baseDir != dir ? dir : null;
            var existing = Directory.GetDirectories(dir).Where(d =>
            {
                var k = BaseKey(IdTag.Replace(System.IO.Path.GetFileName(d), string.Empty));
                return k == BaseKey(stem) || k == BaseKey(title);
            }).ToList();
            if (existing.Count == 1 && !Directory.GetFiles(existing[0]).Any(IsVideo))
            {
                target = existing[0];
                final = opt.Rename || flattenFrom != null
                    ? System.IO.Path.Combine(baseDir, opt.Rename ? title : System.IO.Path.GetFileName(existing[0]))
                    : target;
            }
            else
            {
                target = final = System.IO.Path.Combine(baseDir, title);
                if (Directory.Exists(target) && Directory.GetFiles(target).Any(IsVideo))
                {
                    p.Warnings.Add($"Skipped: folder '{title}' already holds another video (duplicate or wrong match?)");
                    p.FinalPath = m.Path;
                    return p;
                }

                MkDir(p, ctx, target);
            }
        }

        // 2. video + sidecars with the same name
        var newStem = opt.Rename ? names.File : stem;
        var handled = new List<string> { m.Path };
        handled.AddRange(Sidecars(m.Path, siblings));
        foreach (var f in handled)
        {
            var name = System.IO.Path.GetFileName(f);
            Move(p, ctx, f, System.IO.Path.Combine(target, newStem + name.Substring(stem.Length)), "move");
        }

        // 3. subtitles that don't match the video (safe only in a one-movie folder)
        if (opt.Rename && layout == "single")
        {
            PlanOrphanSubs(p, ctx, dir, target, handled, names.File, m.ProductionYear);
        }

        // 4. folder rename (and/or move up out of a collection folder)
        if (final != target)
        {
            Move(p, ctx, target, final, "dirmove",
                System.IO.Path.GetDirectoryName(final) != System.IO.Path.GetDirectoryName(target) ? "flatten" : null);
        }

        // 4b. the collection folder goes once it's empty (checked when applying, so the last movie out removes it)
        if (flattenFrom != null)
        {
            p.Ops.Add(new PlanOp { Kind = "rmdir", To = flattenFrom, Note = "if empty after flatten" });
        }

        p.FinalPath = System.IO.Path.Combine(final, newStem + System.IO.Path.GetExtension(m.Path));

        // 5. NFO so the identification lives next to the file
        if (opt.Nfo)
        {
            var hasNfo = File.Exists(System.IO.Path.Combine(dir, stem + ".nfo"))
                || (layout == "single" && siblings.Any(f => f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)));
            if (!(ids.ContainsKey("Tmdb") || ids.ContainsKey("Imdb")))
            {
                p.Warnings.Add("No NFO: not identified yet (fix the match first)");
            }
            else if (!hasNfo)
            {
                p.Ops.Add(new PlanOp
                {
                    Kind = "nfo",
                    To = System.IO.Path.Combine(final, newStem + ".nfo"),
                    Note = opt.LockNfo ? "locked" : null,
                    Content = NfoXml("movie", m, names.Display ?? m.Name ?? string.Empty, ids, opt.LockNfo),
                });
            }
        }

        return p;
    }

    private static void PlanOrphanSubs(ItemPlan p, Ctx ctx, string dir, string target, List<string> handled, string title, int? movieYear)
    {
        var subs = Directory.GetFiles(dir)
            .Where(f => SubExt.Contains(System.IO.Path.GetExtension(f)) && !handled.Contains(f) && !IsJunk(f))
            .ToList();
        foreach (var sub in Directory.GetDirectories(dir).Where(d => !IsJunk(d) && !HasVideosBelow(d)))
        {
            subs.AddRange(Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories)
                .Where(f => SubExt.Contains(System.IO.Path.GetExtension(f)) && !IsJunk(f)));
        }

        var year = movieYear?.ToString(CultureInfo.InvariantCulture);
        foreach (var s in subs.OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = System.IO.Path.GetFileName(s);
            if (name.StartsWith(title, StringComparison.Ordinal))
            {
                continue;
            }

            var years = Regex.Matches(name, @"(?<!\d)(19\d\d|20\d\d)(?!\d)").Select(x => x.Value).ToList();
            if (year != null && years.Count > 0 && !years.Contains(year))
            {
                p.Warnings.Add($"Subtitle looks like another movie ({years[0]}), left alone: {name}");
                continue;
            }

            var ext = System.IO.Path.GetExtension(s).ToLowerInvariant();
            var baseName = title + LangSuffix(name);
            var dst = System.IO.Path.Combine(target, baseName + ext);
            for (var n = 2; ctx.IsTaken(dst); n++)
            {
                dst = System.IO.Path.Combine(target, $"{baseName}.{n}{ext}");
            }

            Move(p, ctx, s, dst, "move", "subtitle");
        }
    }

    // ------------------------------------------------------------------ series
    /// <summary>One season-folder source: a Jellyfin series folder and its episodes with their season numbers.</summary>
    private sealed record SeasonSource(Series Series, string Dir, List<(Episode Episode, int Season)> Episodes);

    /// <summary>Folders in the same parent that Jellyfin identified as the same show (same TVDB/TMDB/IMDb ID).</summary>
    private static string SeriesPath(BaseItem s) => s.Path.TrimEnd('/');

    private static SeriesGroup? ManualGroup(BaseItem s) =>
        Config.SeriesGroups.FirstOrDefault(g => g.Paths.Contains(SeriesPath(s)));

    private static string IdentityKey(Series s)
    {
        if (Flat)
        {
            return "id:" + s.Id.ToString("N"); // every folder is its own show
        }

        if (ManualGroup(s) is { } manual)
        {
            return "manual:" + manual.Id;
        }

        if (Config.DetachedSeries.Contains(SeriesPath(s)))
        {
            return "id:" + s.Id.ToString("N");
        }

        var parent = System.IO.Path.GetDirectoryName(s.Path.TrimEnd('/')) ?? string.Empty;
        foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
        {
            if (s.ProviderIds.TryGetValue(provider, out var v) && !string.IsNullOrEmpty(v))
            {
                return parent + "|" + provider + ":" + v;
            }
        }

        return "id:" + s.Id.ToString("N");
    }

    private List<ItemPlan> BuildSeriesPlan(PlanOptions opt)
    {
        var roots = LibraryRoots();
        var ctx = new Ctx();
        var plans = new List<ItemPlan>();

        var series = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true,
            })
            .OfType<Series>()
            .Where(s => !string.IsNullOrEmpty(s.Path) && Directory.Exists(s.Path))
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToList();

        var seasonsBySeries = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true,
            })
            .OfType<Season>()
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var episodesBySeries = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false,
            })
            .OfType<Episode>()
            .Where(e => !string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
            .GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Path, StringComparer.Ordinal).ToList());

        var moviesRoot = _library.GetVirtualFolders()
            .Where(f => string.Equals(f.CollectionType?.ToString(), "movies", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Locations)
            .Select(l => l.TrimEnd('/'))
            .FirstOrDefault();

        foreach (var group in series.GroupBy(IdentityKey))
        {
            var members = group.ToList();
            try
            {
                var plan = PlanSeries(members, episodesBySeries, opt, ctx, moviesRoot);
                plan.Seasons = SeasonCards(members, seasonsBySeries, episodesBySeries);
                plan.Root = RootOf(plan.Path, roots);
                plans.Add(plan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Curator: could not plan series {Path}", members[0].Path);
            }
        }

        return plans;
    }

    /// <summary>Jellyfin's season entries for these show folders, with episode counts and artwork info.</summary>
    private static List<SeasonCard> SeasonCards(List<Series> members, Dictionary<Guid, List<Season>> seasonsBySeries, Dictionary<Guid, List<Episode>> episodesBySeries)
    {
        var cards = new List<SeasonCard>();
        foreach (var m in members)
        {
            var eps = episodesBySeries.GetValueOrDefault(m.Id) ?? new List<Episode>();
            foreach (var season in seasonsBySeries.GetValueOrDefault(m.Id) ?? new List<Season>())
            {
                var count = eps.Count(e => e.SeasonId == season.Id);
                if (count == 0)
                {
                    continue; // no episode files: a "missing season" placeholder or an empty leftover
                }

                cards.Add(new SeasonCard
                {
                    ItemId = season.Id.ToString("N"),
                    SeriesItemId = m.Id.ToString("N"),
                    Index = season.IndexNumber,
                    Name = season.Name ?? string.Empty,
                    Overview = season.Overview,
                    EpisodeCount = count,
                    Path = string.IsNullOrEmpty(season.Path) ? null : season.Path.TrimEnd('/'),
                    HasImage = season.HasImage(ImageType.Primary),
                    NameLocked = season.LockedFields.Contains(MetadataField.Name),
                });
            }
        }

        return cards.OrderBy(c => c.Index ?? int.MaxValue).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    private static ItemPlan PlanSeries(List<Series> members, Dictionary<Guid, List<Episode>> episodesBySeries, PlanOptions opt, Ctx ctx, string? moviesRoot)
    {
        // The folder that stays: one already named as the template wants, else the one without a season in its name,
        // else the lowest season / shortest name. Other folders of the same show are merged into it.
        var mainPath = members.Count > 1 ? ManualGroup(members[0])?.MainPath : null;
        var s = members
            .OrderByDescending(m => mainPath != null && SeriesPath(m) == mainPath)
            .ThenByDescending(m => System.IO.Path.GetFileName(m.Path.TrimEnd('/')) == SeriesNames(m).Folder)
            .ThenBy(m => SeasonFromName(System.IO.Path.GetFileName(m.Path.TrimEnd('/'))) ?? 0)
            .ThenBy(m => m.Path.Length)
            .First();
        var others = members.Where(m => m != s).ToList();

        var dir = s.Path.TrimEnd('/');
        var names = SeriesNames(s);
        var ids = Ids(s);
        var allEpisodes = members.SelectMany(m => episodesBySeries.GetValueOrDefault(m.Id) ?? new List<Episode>()).ToList();
        var p = new ItemPlan
        {
            Kind = "series",
            ItemId = s.Id.ToString("N"),
            Path = dir,
            JellyfinName = s.Name,
            OriginalTitle = s.OriginalTitle,
            Year = s.ProductionYear,
            Overview = s.Overview,
            ProviderIds = ids,
            Title = names.Folder,
            FileName = names.Folder,
            TitleSource = names.Source,
            Layout = "series",
            Verified = IsVerified(s),
            Suspicious = !IsVerified(s) && !SeriesIdProviders.Any(ids.ContainsKey),
            Locked = s.IsLocked,
            EpisodeCount = allEpisodes.Count,
            MergedFolders = others.Select(o => o.Path.TrimEnd('/')).ToList(),
        };
        (p.GuessName, p.GuessYear) = GuessFromName(System.IO.Path.GetFileName(dir));
        p.Detached = Config.DetachedSeries.Contains(dir);
        if (members.Count > 1)
        {
            p.GroupKind = ManualGroup(s) != null ? "manual" : "auto";
            p.Members = members
                .OrderByDescending(m => m == s)
                .ThenBy(m => SeasonFromName(System.IO.Path.GetFileName(SeriesPath(m))) ?? 0)
                .ThenBy(m => SeriesPath(m), StringComparer.Ordinal)
                .Select(m =>
                {
                    var eps = episodesBySeries.GetValueOrDefault(m.Id) ?? new List<Episode>();
                    var hint = SeasonFromName(System.IO.Path.GetFileName(SeriesPath(m)));
                    var guess = GuessFromName(System.IO.Path.GetFileName(SeriesPath(m)));
                    return new GroupMember
                    {
                        ItemId = m.Id.ToString("N"),
                        Path = SeriesPath(m),
                        JellyfinName = m.Name ?? string.Empty,
                        OriginalTitle = m.OriginalTitle,
                        Year = m.ProductionYear,
                        ProviderIds = Ids(m),
                        EpisodeCount = eps.Count,
                        Season = eps.Count > 0 ? SeasonFor(eps[0], CustomSeason(m), hint).Season : hint,
                        IsMain = m == s,
                        Locked = m.IsLocked,
                        GuessName = guess.Name,
                        GuessYear = guess.Year,
                    };
                })
                .ToList();
        }

        var final = opt.Rename ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, names.Folder) : dir;
        p.FinalPath = final;

        // 0. container folder ("Fate Series/" holding Fate Zero, UBW…): move each show up to the library root.
        //    After the rescan each is identified on its own; shows that turn out to be the same are then merged
        //    (as seasons) by the same-ID merge below. Nothing else is done to the container itself.
        if (members.Count == 1 && ContainerChildren(dir) is { } kids)
        {
            p.ContainerShows = kids.Select(k => System.IO.Path.GetFileName(k)).ToList();
            if (opt.Flatten)
            {
                var parentDir = System.IO.Path.GetDirectoryName(dir)!;
                foreach (var kid in kids)
                {
                    var name = FlattenName(System.IO.Path.GetFileName(dir), System.IO.Path.GetFileName(kid));
                    Move(p, ctx, kid, System.IO.Path.Combine(parentDir, name), "dirmove", "flatten");
                }

                p.Ops.Add(new PlanOp { Kind = "rmdir", To = dir, Note = "if empty after flatten" });
                var leftovers = Directory.GetFiles(dir).Where(f => !IsJunk(f)).Select(System.IO.Path.GetFileName).ToList();
                if (leftovers.Count > 0)
                {
                    p.Warnings.Add($"'{System.IO.Path.GetFileName(dir)}' keeps {leftovers.Count} file(s) ({string.Join(", ", leftovers.Take(3))}), so it stays; delete it by hand afterwards.");
                }

                p.Warnings.Add($"Container folder: its {kids.Count} shows move up and become separate shows. After the library scan, "
                    + "identify each one; shows that turn out to be the same are then combined here as seasons.");
                return p;
            }

            p.Warnings.Add($"This folder holds {kids.Count} separate shows ({string.Join(", ", p.ContainerShows.Take(3))}…). "
                + "Jellyfin treats them as one show; turn on 'Flatten collection folders' to split them.");
        }

        // 1. display title
        AddDisplayOp(p, s, names.Display);

        // 2. season of every episode, per source folder (each folder can carry its own "Season 2" hint)
        var sources = new List<SeasonSource>();
        foreach (var m in members)
        {
            var mDir = m.Path.TrimEnd('/');
            var custom = CustomSeason(m);
            var hint = members.Count > 1
                ? SeasonFromName(System.IO.Path.GetFileName(mDir))
                : SeasonFromName(m.Name, System.IO.Path.GetFileName(mDir), m.OriginalTitle);
            var rawEps = episodesBySeries.GetValueOrDefault(m.Id) ?? new List<Episode>();

            // subfolders of this series folder: their season numbers, or "separate show" (moved out)
            var subSeasons = new Dictionary<string, (int? Season, string Source)>(StringComparer.Ordinal);
            var excludedDirs = new List<string>();
            var subs = Directory.GetDirectories(mDir).Where(d => !IsJunk(d) && HasVideosBelow(d)).OrderBy(d => d, StringComparer.Ordinal).ToList();
            foreach (var sub in subs)
            {
                var inSub = rawEps.Where(e => IsUnder(e.Path, sub)).ToList();
                var fs = FolderSeason(sub, inSub);
                var rule = FolderRuleFor(sub);
                var action = rule?.Action == "show"
                    || (Flat && rule == null && !ExtrasLike.IsMatch(System.IO.Path.GetFileName(sub).Trim()) && VideoCount(sub) >= 2)
                    ? "show" : "season";
                subSeasons[sub] = fs;
                p.SeasonFolders.Add(new SeasonFolderInfo
                {
                    Path = sub,
                    VideoCount = Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Count(IsVideo),
                    Season = fs.Season,
                    SeasonSource = fs.Source,
                    Action = action,
                });
                if (action == "show")
                {
                    excludedDirs.Add(sub);
                    var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(mDir)!, FlattenName(System.IO.Path.GetFileName(mDir), System.IO.Path.GetFileName(sub)));
                    Move(p, ctx, sub, target, "dirmove", "flatten");
                }
            }

            if (p.SeasonFolders.Count < 2 && members.Count == 1)
            {
                p.SeasonFolders.Clear(); // a single subfolder isn't worth a panel
            }

            // unnumbered videos directly in the series folder (movies / specials): leave, or move to the Movies library
            var excludedFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in rawEps.Where(e => System.IO.Path.GetDirectoryName(e.Path) == mDir && e.IndexNumber == null))
            {
                var action = FolderRuleFor(e.Path)?.Action == "movies" ? "movies" : "leave";
                p.LooseVideos.Add(new LooseVideoInfo { Path = e.Path, Action = action });
                if (action == "movies" && moviesRoot != null)
                {
                    excludedFiles.Add(e.Path);
                    var stem = System.IO.Path.GetFileNameWithoutExtension(e.Path);
                    var movieDir = System.IO.Path.Combine(moviesRoot, Safe(CleanName(stem)));
                    MkDir(p, ctx, movieDir);
                    foreach (var f in new[] { e.Path }.Concat(Sidecars(e.Path, Directory.GetFiles(mDir))))
                    {
                        Move(p, ctx, f, System.IO.Path.Combine(movieDir, System.IO.Path.GetFileName(f)), "move", "to Movies library");
                    }
                }
                else if (action == "movies")
                {
                    p.Warnings.Add("No Movies library found to move videos into.");
                }
            }

            var eps = rawEps
                .Where(e => !excludedFiles.Contains(e.Path) && !excludedDirs.Any(d => IsUnder(e.Path, d)))
                .Select(e =>
                {
                    var sub = subSeasons.Keys.FirstOrDefault(d => IsUnder(e.Path, d));
                    if (sub != null && subSeasons[sub].Season is int fs && subSeasons[sub].Source is "custom" or "name" or "jellyfin")
                    {
                        // a subfolder decides its episodes' season, unless Jellyfin says 2+ for the episode itself
                        return (e, e.ParentIndexNumber is int pn && pn != 1 && subSeasons[sub].Source != "custom" ? pn : fs);
                    }

                    return (e, SeasonFor(e, custom, hint).Season);
                })
                .ToList();
            sources.Add(new SeasonSource(m, mDir, eps));

            var unplaced = eps.Select(x => x.e).Where(e => e.ParentIndexNumber is null).ToList();
            p.UnplacedEpisodes += unplaced.Count;
            if (m == s && unplaced.Count > 0)
            {
                (var season, var source) = SeasonFor(unplaced[0], custom, hint);
                p.Season = season;
                p.SeasonSource = source;
            }
        }

        // 2. episodes -> "<series>/<season folder>/<episode name>", merging the other folders of the same show
        // Grouped folders, or a series whose subfolders you numbered, are laid out as "<series>/<season folder>/".
        // File names only change when "Rename episodes" is on.
        var hasFolderRules = sources.Any(src => Config.FolderRules.Any(r => IsUnder(r.Path, src.Dir)));
        if (opt.Episodes || others.Count > 0 || hasFolderRules)
        {
            PlanEpisodes(p, ctx, s, dir, sources, names.Folder, opt.Episodes);
        }

        // 3. series folder rename (after episode moves, which use the old path)
        if (final != dir)
        {
            Move(p, ctx, dir, final, "dirmove");
        }

        // 4. tvshow.nfo
        if (opt.Nfo)
        {
            var hasNfo = File.Exists(System.IO.Path.Combine(dir, "tvshow.nfo"));
            if (!SeriesIdProviders.Any(ids.ContainsKey))
            {
                p.Warnings.Add("No NFO: not identified yet (fix the match first)");
            }
            else if (!hasNfo)
            {
                p.Ops.Add(new PlanOp
                {
                    Kind = "nfo",
                    To = System.IO.Path.Combine(final, "tvshow.nfo"),
                    Note = opt.LockNfo ? "locked" : null,
                    Content = NfoXml("tvshow", s, names.Display ?? s.Name ?? string.Empty, ids, opt.LockNfo),
                });
            }
        }

        return p;
    }

    private static void PlanEpisodes(ItemPlan p, Ctx ctx, Series s, string dir, List<SeasonSource> sources, string seriesFolder, bool renameFiles)
    {
        var seasonTpl = Flat ? string.Empty : Config.SeasonFolderTemplate ?? DefaultSeasonFolder; // empty = episodes directly in the show folder
        if (string.IsNullOrWhiteSpace(seasonTpl) && sources.Count > 1)
        {
            seasonTpl = "Season {season}"; // merged folders always need a season folder each
        }
        var episodeTpl = Flat ? Or(Config.FlatEpisodeTemplate, "{title} - {episode:00}") : Or(Config.EpisodeTemplate, DefaultEpisodeFile);
        var all = sources.SelectMany(x => x.Episodes.Select(e => (x.Dir, e.Episode, e.Season))).ToList();
        var numbered = all.Where(x => x.Episode.IndexNumber.HasValue && IsUnder(x.Episode.Path, x.Dir)).ToList();
        p.EpisodesSkipped = all.Count - numbered.Count;
        if (p.EpisodesSkipped > 0)
        {
            p.Warnings.Add($"{p.EpisodesSkipped} episode file(s) left as they are: Jellyfin has no episode number for them "
                + "(or they sit outside the series folder).");
        }

        // Safety: several Jellyfin season folders ending up as the same season number, or two files with the same
        // season+episode, would give wrong or colliding names. Leave the episodes of such a show alone.
        var mergedSeasons = numbered.GroupBy(x => x.Season)
            .Where(g => g.Select(x => x.Episode.SeasonId).Distinct().Count() > 1).Select(g => g.Key).ToList();
        var duplicates = numbered.GroupBy(x => (x.Season, x.Episode.IndexNumber)).Count(g => g.Count() > 1);
        if (mergedSeasons.Count > 0 || duplicates > 0)
        {
            p.SeasonConflict = true;
            p.Warnings.Add("Episodes not renamed: the season numbering looks wrong"
                + (mergedSeasons.Count > 0 ? $" (several season folders would all be season {string.Join("/", mergedSeasons)})" : string.Empty)
                + (duplicates > 0 ? $" ({duplicates} season+episode numbers used by more than one file)" : string.Empty)
                + ". Fix the match or set the season per folder; the folder name and NFO can still be applied.");
            return;
        }

        var pad = numbered.Any(x => (x.Episode.IndexNumberEnd ?? x.Episode.IndexNumber) >= 100) ? "000" : "00";
        var moved = new HashSet<string>(StringComparer.Ordinal);
        var destDirs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (srcDir, e, season) in numbered)
        {
            var number = e.IndexNumber!.Value;
            var end = e.IndexNumberEnd is int x && x > number ? x : (int?)null;
            var tokens = ItemTokens(s);
            tokens["series"] = seriesFolder;
            tokens["seriesTitle"] = s.Name ?? string.Empty;
            tokens["season"] = season;
            tokens["episode"] = number;
            tokens["episodeEnd"] = end;
            tokens["episodes"] = "E" + number.ToString(pad, CultureInfo.InvariantCulture)
                + (end is int en ? "-E" + en.ToString(pad, CultureInfo.InvariantCulture) : string.Empty);
            tokens["episodeTitle"] = e.Name ?? string.Empty;
            // a merged folder's Jellyfin season title is just its folder name, so only use it inside the main folder
            tokens["seasonTitle"] = srcDir == dir ? SeasonTitle(e) : string.Empty;
            var stem = System.IO.Path.GetFileNameWithoutExtension(e.Path);

            var seasonName = string.IsNullOrWhiteSpace(seasonTpl) ? string.Empty : Safe(Render(seasonTpl, tokens));
            var seasonDir = seasonName.Length == 0 ? dir : System.IO.Path.Combine(dir, seasonName);
            destDirs.Add(seasonDir);
            var newStem = renameFiles ? Safe(Render(episodeTpl, tokens)) : stem;
            if (newStem.Length == 0)
            {
                continue;
            }

            var oldDir = System.IO.Path.GetDirectoryName(e.Path)!;
            if (oldDir == seasonDir && stem == newStem)
            {
                continue; // already tidy
            }

            MkDir(p, ctx, seasonDir);
            var files = new List<string> { e.Path };
            files.AddRange(Sidecars(e.Path, Directory.GetFiles(oldDir)));
            foreach (var f in files)
            {
                var dst = System.IO.Path.Combine(seasonDir, newStem + System.IO.Path.GetFileName(f).Substring(stem.Length));
                if (Move(p, ctx, f, dst, "move"))
                {
                    moved.Add(f);
                }
            }
        }

        // folders that end up completely empty are removed, including merged series folders (undo recreates them)
        foreach (var src in sources)
        {
            var candidates = Directory.GetDirectories(src.Dir, "*", SearchOption.AllDirectories).ToList();
            if (src.Dir != dir)
            {
                candidates.Add(src.Dir);
            }

            foreach (var sub in candidates.OrderByDescending(d => d.Length))
            {
                var isDestination = destDirs.Any(d => d == sub || IsUnder(d, sub));
                var leftovers = Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Where(f => !moved.Contains(f));
                if (!isDestination && !leftovers.Any() && !ctx.Taken.Contains(sub))
                {
                    p.Ops.Add(new PlanOp { Kind = "rmdir", To = sub, Note = "empty after moves" });
                }
            }
        }

        foreach (var other in sources.Where(x => x.Dir != dir))
        {
            var left = Directory.EnumerateFiles(other.Dir, "*", SearchOption.AllDirectories).Count(f => !moved.Contains(f));
            if (left > 0)
            {
                p.Warnings.Add($"'{System.IO.Path.GetFileName(other.Dir)}' keeps {left} other file(s) (artwork, extras...), so that folder stays.");
            }
        }
    }

    // ------------------------------------------------------------------ NFO
    private static string NfoXml(string root, BaseItem item, string title, Dictionary<string, string> ids, bool lockData)
    {
        var x = new XElement(root, new XElement("title", title));
        if (!string.IsNullOrEmpty(item.OriginalTitle))
        {
            x.Add(new XElement("originaltitle", item.OriginalTitle));
        }

        if (item.ProductionYear is int y)
        {
            x.Add(new XElement("year", y));
        }

        if (!string.IsNullOrEmpty(item.Overview))
        {
            x.Add(new XElement("plot", item.Overview));
        }

        if (lockData)
        {
            x.Add(new XElement("lockdata", "true"));
        }

        var first = true;
        foreach (var provider in new[] { "Tmdb", "Tvdb", "Imdb", "AniList", "AniDB" })
        {
            if (ids.TryGetValue(provider, out var v))
            {
                var uid = new XElement("uniqueid", new XAttribute("type", provider.ToLowerInvariant()), v);
                if (first && provider != "AniList" && provider != "AniDB")
                {
                    uid.Add(new XAttribute("default", "true"));
                    first = false;
                }

                x.Add(uid);
                if (provider is "Tmdb" or "Tvdb" or "Imdb")
                {
                    x.Add(new XElement(provider.ToLowerInvariant() + "id", v));
                }
            }
        }

        if (ids.TryGetValue("TmdbCollection", out var coll))
        {
            x.Add(new XElement("tmdbcollectionid", coll));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), x).Declaration + "\n" + x + "\n";
    }

    // ------------------------------------------------------------------ per-folder rules inside a series
    private static FolderRule? FolderRuleFor(string path) => Config.FolderRules.FirstOrDefault(r => r.Path == path);

    /// <summary>Save what to do with a subfolder (season number / separate show) or a loose video (Movies library).</summary>
    public bool SetFolderRule(string path, string action, int? season)
    {
        path = path.TrimEnd('/');
        if (!LibraryRoots().Any(r => IsUnder(path, r)) || !(Directory.Exists(path) || File.Exists(path)))
        {
            return false;
        }

        Config.FolderRules.RemoveAll(r => r.Path == path);
        var automatic = (action == "season" && season == null) || action == "leave";
        if (!automatic)
        {
            Config.FolderRules.Add(new FolderRule { Path = path, Action = action, Season = season });
        }

        Plugin.Instance!.SaveConfiguration();
        return true;
    }

    /// <summary>Season of a subfolder: your number, else Jellyfin's when it isn't 1, else a number in its name, else Jellyfin's 1.</summary>
    private static (int? Season, string Source) FolderSeason(string sub, IEnumerable<Episode> inSub)
    {
        if (FolderRuleFor(sub)?.Season is int custom)
        {
            return (custom, "custom");
        }

        var jf = inSub.Select(e => e.Season?.IndexNumber ?? e.ParentIndexNumber).FirstOrDefault(x => x != null);
        if (jf is int j && j != 1)
        {
            return (j, "jellyfin");
        }

        if (SeasonFromName(System.IO.Path.GetFileName(sub)) is int hint)
        {
            return (hint, "name");
        }

        return jf is int one ? (one, "jellyfin") : (null, "unset");
    }

    // ------------------------------------------------------------------ manual series grouping
    private List<Series> SeriesByIds(IEnumerable<string> itemIds) => itemIds
        .Select(id => Guid.TryParse(id, out var g) ? _library.GetItemById(g) as Series : null)
        .OfType<Series>()
        .ToList();

    private static void DropFromGroups(ICollection<string> paths)
    {
        foreach (var g in Config.SeriesGroups)
        {
            g.Paths.RemoveAll(paths.Contains);
            if (g.MainPath != null && paths.Contains(g.MainPath))
            {
                g.MainPath = null;
            }
        }

        Config.SeriesGroups.RemoveAll(g => g.Paths.Count < 2);
    }

    /// <summary>Combine these series folders into one show (any IDs). mainItemId picks the folder that is kept.</summary>
    public int CombineSeries(IEnumerable<string> itemIds, string? mainItemId)
    {
        var items = SeriesByIds(itemIds);
        var paths = items.Select(SeriesPath).Distinct().ToList();
        if (paths.Count < 2)
        {
            return 0;
        }

        DropFromGroups(paths);
        Config.DetachedSeries.RemoveAll(paths.Contains);
        var main = items.FirstOrDefault(i => i.Id.ToString("N") == mainItemId?.Replace("-", string.Empty, StringComparison.Ordinal));
        Config.SeriesGroups.Add(new SeriesGroup { Paths = paths, MainPath = main == null ? null : SeriesPath(main) });
        Plugin.Instance!.SaveConfiguration();
        return paths.Count;
    }

    /// <summary>Take one folder out of its group (manual or automatic); it becomes its own show.</summary>
    public void DetachSeries(string itemId)
    {
        var s = SeriesByIds(new[] { itemId }).FirstOrDefault();
        if (s == null)
        {
            return;
        }

        var path = SeriesPath(s);
        DropFromGroups(new List<string> { path });
        if (!Config.DetachedSeries.Contains(path))
        {
            Config.DetachedSeries.Add(path);
        }

        Plugin.Instance!.SaveConfiguration();
    }

    /// <summary>Forget manual grouping/detaching for these folders (back to automatic same-ID grouping).</summary>
    public void ResetSeriesGrouping(IEnumerable<string> itemIds)
    {
        var paths = SeriesByIds(itemIds).Select(SeriesPath).ToList();
        DropFromGroups(paths);
        Config.DetachedSeries.RemoveAll(paths.Contains);
        Plugin.Instance!.SaveConfiguration();
    }

    // ------------------------------------------------------------------ NFO maintenance for re-identify
    /// <summary>NFO files that describe this item: "&lt;video&gt;.nfo" / "movie.nfo" for a movie, "tvshow.nfo" for a series.</summary>
    private static List<string> NfoPaths(BaseItem item)
    {
        if (item is Series)
        {
            return new List<string> { System.IO.Path.Combine(item.Path.TrimEnd('/'), "tvshow.nfo") };
        }

        var dir = System.IO.Path.GetDirectoryName(item.Path) ?? string.Empty;
        return new List<string>
        {
            System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(item.Path) + ".nfo"),
            System.IO.Path.Combine(dir, "movie.nfo"),
        };
    }

    /// <summary>
    /// Remove the item's NFO before re-identifying: Jellyfin reads local NFOs first, so an NFO holding the old IDs
    /// would pull the item straight back to the old (wrong) match. Returns how many files were removed.
    /// </summary>
    /// <summary>Contents of the item's NFO files (path -> text), so a force stop can put them back.</summary>
    public Dictionary<string, string> BackupNfo(BaseItem item) =>
        NfoPaths(item).Where(File.Exists).ToDictionary(p => p, p => File.ReadAllText(p));

    /// <summary>Roll back: remove NFOs written since the backup, then write the backed-up ones again.</summary>
    public void RestoreNfo(BaseItem item, Dictionary<string, string> backup)
    {
        foreach (var path in NfoPaths(item).Where(p => File.Exists(p) && !backup.ContainsKey(p)))
        {
            File.Delete(path);
        }

        foreach (var (path, text) in backup)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }

    public int RemoveNfo(BaseItem item)
    {
        var n = 0;
        foreach (var path in NfoPaths(item).Where(File.Exists))
        {
            File.Delete(path);
            n++;
        }

        _logger.LogInformation("Curator: removed {Count} NFO file(s) for {Name} before re-identify", n, item.Name);
        return n;
    }

    /// <summary>Write a fresh NFO from the item's current metadata (after re-identify).</summary>
    public string WriteNfo(BaseItem item, bool lockData)
    {
        var path = NfoPaths(item)[0];
        var root = item is Series ? "tvshow" : "movie";
        var display = item is Series ? DisplayFor(item, Config.SeriesDisplayTemplate) : DisplayFor(item, Config.DisplayTemplate);
        File.WriteAllText(path, NfoXml(root, item, display ?? item.Name ?? string.Empty, Ids(item), lockData), new UTF8Encoding(false));
        return path;
    }

    // ------------------------------------------------------------------ lock
    /// <summary>Lock/unlock items. For a series, its seasons and episodes are locked too.</summary>
    public async Task<int> SetLocked(IEnumerable<string> itemIds, bool locked, CancellationToken ct)
    {
        var n = 0;
        foreach (var id in itemIds)
        {
            if (!Guid.TryParse(id, out var guid) || _library.GetItemById(guid) is not { } item)
            {
                continue;
            }

            var targets = new List<BaseItem> { item };
            if (item is Series)
            {
                targets.AddRange(_library.GetItemList(new InternalItemsQuery { AncestorIds = new[] { item.Id }, Recursive = true }));
            }

            foreach (var t in targets.Where(t => t.IsLocked != locked))
            {
                t.IsLocked = locked;
                await t.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }

            n++;
        }

        _logger.LogInformation("Curator: {Action} {Count} item(s)", locked ? "locked" : "unlocked", n);
        return n;
    }

    // ------------------------------------------------------------------ apply / undo
    private static void Rename(string src, string dst, bool isDir)
    {
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) && src != dst)
        {
            var tmp = src + ".curator_tmp"; // case-only rename on case-insensitive filesystems (exFAT)
            if (isDir) Directory.Move(src, tmp); else File.Move(src, tmp);
            src = tmp;
        }
        else if (File.Exists(dst) || Directory.Exists(dst))
        {
            throw new IOException($"Target exists: {dst}");
        }

        if (isDir) Directory.Move(src, dst); else File.Move(src, dst);
    }

    /// <summary>Set the title Jellyfin shows and lock (or unlock) the Name field so refreshes keep it.</summary>
    /// <summary>
    /// Set a season's title (locked, so refreshes keep it). An empty name unlocks it again so the metadata source's
    /// title comes back on the next refresh.
    /// </summary>
    public async Task<bool> SetSeasonName(string itemId, string? name, CancellationToken ct)
    {
        if (!Guid.TryParse(itemId, out var id) || _library.GetItemById(id) is not Season season)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            season.LockedFields = season.LockedFields.Where(f => f != MetadataField.Name).ToArray();
            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }
        else
        {
            await SetDisplayName(itemId, name.Trim(), true, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Curator: season {Id} title set to {Name}", itemId, string.IsNullOrWhiteSpace(name) ? "(automatic)" : name);
        return true;
    }

    private async Task SetDisplayName(string? itemId, string name, bool lockName, CancellationToken ct)
    {
        if (!Guid.TryParse(itemId, out var id) || _library.GetItemById(id) is not { } item)
        {
            throw new InvalidOperationException("Item no longer in the library (it may have been re-added after a move).");
        }

        item.Name = name;
        var locked = item.LockedFields.Where(f => f != MetadataField.Name).ToList();
        if (lockName)
        {
            locked.Add(MetadataField.Name);
        }

        item.LockedFields = locked.ToArray();
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }

    public async Task<ApplyResult> Apply(IReadOnlyCollection<string> itemIds, PlanOptions opt, CancellationToken ct, Action<int, int, string>? report = null)
    {
        var result = new ApplyResult();
        var log = new List<PlanOp>();
        var wanted = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = BuildPlan(opt).Where(p => wanted.Contains(p.ItemId)).ToList();
        var total = selected.Sum(p => p.Ops.Count);
        var n = 0;
        report?.Invoke(0, total, "starting");
        foreach (var plan in selected)
        {
            foreach (var op in plan.Ops)
            {
                report?.Invoke(++n, total, $"{plan.Title}: {op.Kind} {System.IO.Path.GetFileName(op.To)}");
                try
                {
                    switch (op.Kind)
                    {
                        case "mkdir":
                            if (Directory.Exists(op.To)) continue;
                            Directory.CreateDirectory(op.To);
                            break;
                        case "move":
                            Rename(op.From, op.To, false);
                            break;
                        case "dirmove":
                            Rename(op.From, op.To, true);
                            break;
                        case "rmdir":
                            if (!Directory.Exists(op.To) || Directory.EnumerateFileSystemEntries(op.To).Any()) continue;
                            Directory.Delete(op.To);
                            break;
                        case "nfo":
                            if (File.Exists(op.To)) continue;
                            File.WriteAllText(op.To, op.Content, new UTF8Encoding(false));
                            break;
                        case "display":
                            await SetDisplayName(op.ItemId, op.To, true, ct).ConfigureAwait(false);
                            break;
                    }

                    log.Add(new PlanOp { Kind = op.Kind, From = op.From, To = op.To, ItemId = op.ItemId });
                    result.Done++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{op.Kind} {op.From} -> {op.To}: {ex.Message}");
                    _logger.LogError(ex, "Curator: {Kind} failed for {From}", op.Kind, op.From);
                }
            }
        }

        if (log.Count > 0)
        {
            Directory.CreateDirectory(LogDir);
            var path = System.IO.Path.Combine(LogDir, $"apply-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }));
            PruneHistory();
            result.Log = path;
            _logger.LogInformation("Curator: applied {Count} changes, log {Log}", log.Count, path);
            ScanOrDefer(log.Count);
        }

        return result;
    }

    private static readonly Regex LogName = new(@"^apply-(\d{8})-(\d{6})$");

    private static string LogId(string file) => System.IO.Path.GetFileNameWithoutExtension(file);

    /// <summary>Folder-change history, newest first (as many as the "Undo history" setting keeps).</summary>
    public List<HistoryEntry> History()
    {
        var list = new List<HistoryEntry>();
        if (!Directory.Exists(LogDir))
        {
            return list;
        }

        foreach (var f in Directory.GetFiles(LogDir, "apply-*.*")
                     .Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".undone", StringComparison.Ordinal))
                     .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal))
        {
            List<PlanOp> ops;
            try
            {
                ops = JsonSerializer.Deserialize<List<PlanOp>>(File.ReadAllText(f)) ?? new();
            }
            catch (Exception)
            {
                continue; // unreadable log: skip it
            }

            var id = LogId(f);
            var m = LogName.Match(id);
            list.Add(new HistoryEntry
            {
                Id = id,
                Time = m.Success
                    ? DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture).ToString("s", CultureInfo.InvariantCulture)
                    : File.GetLastWriteTime(f).ToString("s", CultureInfo.InvariantCulture),
                Undone = f.EndsWith(".undone", StringComparison.Ordinal),
                Total = ops.Count,
                Kinds = ops.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count()),
                // the folders this run touched (movie / series / season folder names), for a quick overview
                Items = ops.Where(o => o.Kind is "move" or "dirmove")
                    .Select(o => System.IO.Path.GetFileName(o.Kind == "dirmove" ? o.To : System.IO.Path.GetDirectoryName(o.To) ?? string.Empty))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .Take(12)
                    .ToList(),
                Ops = ops.Take(300).ToList(),
            });
        }

        return list;
    }

    /// <summary>Undo the most recent folder change that hasn't been undone yet.</summary>
    public async Task<ApplyResult> UndoLast(CancellationToken ct, Action<int, int, string>? report = null)
    {
        var last = Directory.Exists(LogDir)
            ? Directory.GetFiles(LogDir, "apply-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (last == null)
        {
            return new ApplyResult { Errors = { "Nothing to undo." } };
        }

        var lastTotal = CountOps(last);
        var lastN = 0;
        var result = await UndoLog(last, ct, text => report?.Invoke(++lastN, lastTotal, text)).ConfigureAwait(false);
        PruneHistory();
        ScanOrDefer(result.Done);
        return result;
    }

    /// <summary>
    /// Rewind to just before folder change <paramref name="id"/>: undo it and every applied change after it, newest first.
    /// </summary>
    public async Task<ApplyResult> Rewind(string id, CancellationToken ct, Action<int, int, string>? report = null)
    {
        if (!LogName.IsMatch(id ?? string.Empty) || !Directory.Exists(LogDir))
        {
            return new ApplyResult { Errors = { "Unknown history entry." } };
        }

        var logs = Directory.GetFiles(LogDir, "apply-*.json")
            .Where(f => string.CompareOrdinal(LogId(f), id) >= 0)
            .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
        var total = new ApplyResult();
        if (logs.Count == 0)
        {
            total.Errors.Add("Nothing to rewind: that change and everything after it are already undone.");
            return total;
        }

        var rewindTotal = logs.Sum(CountOps);
        var rewindN = 0;
        foreach (var log in logs)
        {
            var r = await UndoLog(log, ct, text => report?.Invoke(++rewindN, rewindTotal, text)).ConfigureAwait(false);
            total.Done += r.Done;
            total.Errors.AddRange(r.Errors);
        }

        total.Log = string.Join(", ", logs.Select(LogId));
        _logger.LogInformation("Curator: rewound {Count} folder change(s) back to before {Id}", logs.Count, id);
        PruneHistory();
        ScanOrDefer(total.Done);
        return total;
    }

    /// <summary>Reverse one apply log (newest operation first) and mark it undone.</summary>
    private static int CountOps(string file)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<PlanOp>>(File.ReadAllText(file)) ?? new()).Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private async Task<ApplyResult> UndoLog(string file, CancellationToken ct, Action<string>? onOp = null)
    {
        var result = new ApplyResult();
        var log = JsonSerializer.Deserialize<List<PlanOp>>(File.ReadAllText(file)) ?? new();
        foreach (var op in Enumerable.Reverse(log))
        {
            onOp?.Invoke("undo " + op.Kind + " " + System.IO.Path.GetFileName(op.To));
            try
            {
                switch (op.Kind)
                {
                    case "nfo":
                        File.Delete(op.To);
                        break;
                    case "move":
                        Rename(op.To, op.From, false);
                        break;
                    case "dirmove":
                        Rename(op.To, op.From, true);
                        break;
                    case "rmdir":
                        Directory.CreateDirectory(op.To);
                        break;
                    case "mkdir":
                        if (Directory.Exists(op.To) && !Directory.EnumerateFileSystemEntries(op.To).Any()) Directory.Delete(op.To);
                        break;
                    case "display":
                        await SetDisplayName(op.ItemId, op.From, false, ct).ConfigureAwait(false);
                        break;
                }

                result.Done++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"undo {op.Kind} {op.To}: {ex.Message}");
            }
        }

        File.Move(file, System.IO.Path.ChangeExtension(file, ".undone"));
        result.Log = file;
        return result;
    }

    /// <summary>Keep only the newest UndoHistoryLimit apply logs (undone ones count too); -1 keeps everything.</summary>
    private void PruneHistory()
    {
        var limit = Config.UndoHistoryLimit;
        if (limit < 0 || !Directory.Exists(LogDir))
        {
            return;
        }

        var keep = Math.Max(1, limit);
        var old = Directory.GetFiles(LogDir, "apply-*.*")
            .Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".undone", StringComparison.Ordinal))
            .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal) // names are apply-yyyyMMdd-HHmmss
            .Skip(keep)
            .ToList();
        foreach (var f in old)
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Curator: could not delete old apply log {File}", f);
            }
        }

        if (old.Count > 0)
        {
            _logger.LogInformation("Curator: removed {Count} old apply log(s) (keeping {Keep})", old.Count, keep);
        }
    }

    // ------------------------------------------------------------------ library scan (automatic or on demand)
    private static int _pendingChanges;
    private static volatile bool _scanning;
    private static DateTime? _lastScan;

    public static ScanStatus GetScanStatus() => new()
    {
        Pending = _pendingChanges,
        Scanning = _scanning,
        Auto = Config.ScanAfterChanges,
        LastScan = _lastScan?.ToString("o", CultureInfo.InvariantCulture),
    };

    /// <summary>After folder changes: scan now, or (when automatic scanning is off) remember them for "Scan library".</summary>
    private void ScanOrDefer(int changes)
    {
        if (changes <= 0)
        {
            return;
        }

        if (Config.ScanAfterChanges)
        {
            StartScan();
        }
        else
        {
            var waiting = Interlocked.Add(ref _pendingChanges, changes);
            _logger.LogInformation("Curator: {Count} folder change(s) waiting for a library scan", waiting);
        }
    }

    /// <summary>Scan all libraries now (one scan at a time). Returns false when a scan is already running.</summary>
    public bool StartScan()
    {
        if (_scanning)
        {
            return false;
        }

        _scanning = true;
        Interlocked.Exchange(ref _pendingChanges, 0);
        _ = Task.Run(async () =>
        {
            try
            {
                await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Curator: library scan failed");
            }
            finally
            {
                _scanning = false;
                _lastScan = DateTime.UtcNow;
            }
        });
        return true;
    }
}
