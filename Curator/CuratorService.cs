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

    /// <summary>Series: other folders Jellyfin identified as the same show; their episodes merge into this one.</summary>
    [JsonPropertyName("mergedFolders")] public List<string> MergedFolders { get; set; } = new();

    /// <summary>Series: Jellyfin's season/episode numbering is inconsistent, so episodes are left alone.</summary>
    [JsonPropertyName("seasonConflict")] public bool SeasonConflict { get; set; }

    [JsonPropertyName("ops")] public List<PlanOp> Ops { get; set; } = new();

    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
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
                    var plan = PlanMovie(movie, dir, siblings, layout, opt, ctx);
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

    private static ItemPlan PlanMovie(Movie m, string dir, List<string> siblings, string layout, PlanOptions opt, Ctx ctx)
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
        if (layout == "single")
        {
            target = dir;
            final = opt.Rename ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, title) : dir;
        }
        else
        {
            var existing = Directory.GetDirectories(dir).Where(d =>
            {
                var k = BaseKey(IdTag.Replace(System.IO.Path.GetFileName(d), string.Empty));
                return k == BaseKey(stem) || k == BaseKey(title);
            }).ToList();
            if (existing.Count == 1 && !Directory.GetFiles(existing[0]).Any(IsVideo))
            {
                target = existing[0];
                final = opt.Rename ? System.IO.Path.Combine(dir, title) : target;
            }
            else
            {
                target = final = System.IO.Path.Combine(dir, title);
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

        // 4. folder rename
        if (final != target)
        {
            Move(p, ctx, target, final, "dirmove");
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
    private static string IdentityKey(Series s)
    {
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

        foreach (var group in series.GroupBy(IdentityKey))
        {
            var members = group.ToList();
            try
            {
                var plan = PlanSeries(members, episodesBySeries, opt, ctx);
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

    private static ItemPlan PlanSeries(List<Series> members, Dictionary<Guid, List<Episode>> episodesBySeries, PlanOptions opt, Ctx ctx)
    {
        // The folder that stays: one already named as the template wants, else the one without a season in its name,
        // else the lowest season / shortest name. Other folders of the same show are merged into it.
        var s = members
            .OrderByDescending(m => System.IO.Path.GetFileName(m.Path.TrimEnd('/')) == SeriesNames(m).Folder)
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
        var final = opt.Rename ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir)!, names.Folder) : dir;
        p.FinalPath = final;

        // 0. display title
        AddDisplayOp(p, s, names.Display);

        // 1. season of every episode, per source folder (each folder can carry its own "Season 2" hint)
        var sources = new List<SeasonSource>();
        foreach (var m in members)
        {
            var mDir = m.Path.TrimEnd('/');
            var custom = CustomSeason(m);
            var hint = members.Count > 1
                ? SeasonFromName(System.IO.Path.GetFileName(mDir))
                : SeasonFromName(m.Name, System.IO.Path.GetFileName(mDir), m.OriginalTitle);
            var eps = (episodesBySeries.GetValueOrDefault(m.Id) ?? new List<Episode>())
                .Select(e => (e, SeasonFor(e, custom, hint).Season)).ToList();
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
        if (opt.Episodes)
        {
            PlanEpisodes(p, ctx, s, dir, sources, names.Folder);
        }
        else if (others.Count > 0)
        {
            p.Warnings.Add($"{others.Count} other folder(s) are the same show in Jellyfin: "
                + string.Join(", ", others.Select(o => System.IO.Path.GetFileName(o.Path.TrimEnd('/'))))
                + ". Turn on 'Rename episodes into season folders' to merge them here as separate seasons.");
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

    private static void PlanEpisodes(ItemPlan p, Ctx ctx, Series s, string dir, List<SeasonSource> sources, string seriesFolder)
    {
        var seasonTpl = Config.SeasonFolderTemplate ?? DefaultSeasonFolder; // set to empty = episodes directly in the series folder
        var episodeTpl = Or(Config.EpisodeTemplate, DefaultEpisodeFile);
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
            // a merged folder's Jellyfin season title is just its folder name, so only use it for the main folder
            tokens["seasonTitle"] = srcDir == dir ? SeasonTitle(e) : string.Empty;

            var seasonName = string.IsNullOrWhiteSpace(seasonTpl) ? string.Empty : Safe(Render(seasonTpl, tokens));
            var seasonDir = seasonName.Length == 0 ? dir : System.IO.Path.Combine(dir, seasonName);
            destDirs.Add(seasonDir);
            var newStem = Safe(Render(episodeTpl, tokens));
            if (newStem.Length == 0)
            {
                continue;
            }

            var oldDir = System.IO.Path.GetDirectoryName(e.Path)!;
            var stem = System.IO.Path.GetFileNameWithoutExtension(e.Path);
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

    public async Task<ApplyResult> Apply(IReadOnlyCollection<string> itemIds, PlanOptions opt, CancellationToken ct)
    {
        var result = new ApplyResult();
        var log = new List<PlanOp>();
        var wanted = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in BuildPlan(opt).Where(p => wanted.Contains(p.ItemId)))
        {
            foreach (var op in plan.Ops)
            {
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
            result.Log = path;
            _logger.LogInformation("Curator: applied {Count} changes, log {Log}", log.Count, path);
            QueueScan();
        }

        return result;
    }

    public async Task<ApplyResult> UndoLast(CancellationToken ct)
    {
        var result = new ApplyResult();
        var last = Directory.Exists(LogDir)
            ? Directory.GetFiles(LogDir, "apply-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (last == null)
        {
            result.Errors.Add("Nothing to undo.");
            return result;
        }

        var log = JsonSerializer.Deserialize<List<PlanOp>>(File.ReadAllText(last)) ?? new();
        foreach (var op in Enumerable.Reverse(log))
        {
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

        File.Move(last, System.IO.Path.ChangeExtension(last, ".undone"));
        result.Log = last;
        QueueScan();
        return result;
    }

    private void QueueScan()
    {
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
        });
    }
}
