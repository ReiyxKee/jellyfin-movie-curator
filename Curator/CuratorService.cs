using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MovieCurator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieCurator.Curator;

public class PlanOptions
{
    [JsonPropertyName("rename")] public bool Rename { get; set; } = true;

    [JsonPropertyName("nfo")] public bool Nfo { get; set; } = true;

    [JsonPropertyName("lockNfo")] public bool LockNfo { get; set; } = true;
}

public class PlanOp
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty; // mkdir | move | dirmove | nfo

    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;

    [JsonPropertyName("note")] public string? Note { get; set; }

    [JsonPropertyName("itemId")] public string? ItemId { get; set; }

    [JsonIgnore] public string? Content { get; set; }
}

public class MoviePlan
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>Library folder this movie lives in, so the UI can show short relative paths.</summary>
    [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;

    [JsonPropertyName("jellyfinName")] public string JellyfinName { get; set; } = string.Empty;

    [JsonPropertyName("originalTitle")] public string? OriginalTitle { get; set; }

    [JsonPropertyName("year")] public int? Year { get; set; }

    [JsonPropertyName("providerIds")] public Dictionary<string, string> ProviderIds { get; set; } = new();

    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;

    /// <summary>New Jellyfin display title from the display template (null = unchanged).</summary>
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }

    [JsonPropertyName("titleSource")] public string TitleSource { get; set; } = "metadata"; // metadata | custom | filename

    [JsonPropertyName("layout")] public string Layout { get; set; } = string.Empty; // loose | single | shared

    [JsonPropertyName("suspicious")] public bool Suspicious { get; set; }

    /// <summary>Jellyfin's "Lock this item" flag: refreshes won't change its metadata.</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; }

    /// <summary>Title/year guessed from the file or folder name, used for "Suggest matches".</summary>
    [JsonPropertyName("guessName")] public string GuessName { get; set; } = string.Empty;

    [JsonPropertyName("guessYear")] public int? GuessYear { get; set; }

    [JsonPropertyName("finalPath")] public string FinalPath { get; set; } = string.Empty;

    [JsonPropertyName("ops")] public List<PlanOp> Ops { get; set; } = new();

    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

public class ApplyResult
{
    [JsonPropertyName("done")] public int Done { get; set; }

    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();

    [JsonPropertyName("log")] public string? Log { get; set; }
}

/// <summary>Same rules as organize_media.py, running inside Jellyfin.</summary>
public class CuratorService
{
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".flv", ".mpg", ".mpeg" };

    private static readonly HashSet<string> SubExt = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup" };

    private static readonly HashSet<string> SidecarExt = new(SubExt, StringComparer.OrdinalIgnoreCase)
        { ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".tbn" };

    private static readonly string[] AnimeProviders = { "AniList", "AniDB", "AniSearch" };

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
                return (CleanSpaces(m.Groups[1].Value), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
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

    private static IEnumerable<string> OverrideKeys(BaseItem item)
    {
        if (item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrEmpty(tmdb))
        {
            yield return "tmdb:" + tmdb;
        }

        if (item.ProviderIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrEmpty(imdb))
        {
            yield return "imdb:" + imdb;
        }

        yield return "path:" + item.Path;
    }

    /// <summary>Fill a naming template: {title}, {originalTitle}, {year}, {tmdb}, {imdb}.</summary>
    public static string Render(string template, BaseItem item)
    {
        string Token(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "title": return item.Name ?? string.Empty;
                case "originaltitle": return string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.Name ?? string.Empty : item.OriginalTitle;
                case "year": return item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                case "tmdb": return item.ProviderIds.TryGetValue("Tmdb", out var t) ? t : string.Empty;
                case "imdb": return item.ProviderIds.TryGetValue("Imdb", out var i) ? i : string.Empty;
                default: return "{" + name + "}";
            }
        }

        var s = Regex.Replace(template, @"\{(\w+)\}", m => Token(m.Groups[1].Value));
        return ApplyRules(TidyEmpty(s));
    }

    /// <summary>Remove leftovers of missing values: "()", "[]", "[imdbid-]", dangling " - ".</summary>
    private static string TidyEmpty(string s)
    {
        s = Regex.Replace(s, @"\(\s*\)|\[\s*\]|\[(imdbid|tmdbid)-\]", string.Empty, RegexOptions.IgnoreCase);
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

    private sealed record Names(string Folder, string File, string? Display, string Source);

    private static Names NamesFor(BaseItem item, string fallback)
    {
        var fromFile = Safe(ApplyRules(CleanName(IdTag.Replace(fallback, " "))));
        foreach (var key in OverrideKeys(item))
        {
            var o = Config.Overrides.FirstOrDefault(x => x.Key == key);
            if (o != null && !string.IsNullOrWhiteSpace(o.Name))
            {
                var custom = Safe(o.Name.Trim());
                return new Names(custom, custom, DisplayFor(item), "custom");
            }
        }

        var identified = !string.IsNullOrWhiteSpace(item.Name)
            && (item.ProviderIds.ContainsKey("Tmdb") || item.ProviderIds.ContainsKey("Imdb"));
        if (!identified)
        {
            return new Names(fromFile, fromFile, null, "filename");
        }

        var folderTpl = string.IsNullOrWhiteSpace(Config.FolderTemplate) ? "{originalTitle} ({year})" : Config.FolderTemplate;
        var folder = Safe(Render(folderTpl, item));
        if (folder.Length == 0)
        {
            folder = fromFile;
        }

        var file = string.IsNullOrWhiteSpace(Config.FileTemplate) ? folder : Safe(Render(Config.FileTemplate, item));
        return new Names(folder, file.Length > 0 ? file : folder, DisplayFor(item), "metadata");
    }

    private static string? DisplayFor(BaseItem item)
    {
        if (string.IsNullOrWhiteSpace(Config.DisplayTemplate) || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        var d = Render(Config.DisplayTemplate, item);
        return d.Length > 0 ? d : null;
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

    // ------------------------------------------------------------------ planning
    private sealed class Ctx
    {
        public HashSet<string> Taken { get; } = new(StringComparer.Ordinal);

        public bool IsTaken(string p) => Taken.Contains(p) || File.Exists(p) || Directory.Exists(p);
    }

    public List<Movie> Movies() =>
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

    public List<MoviePlan> BuildPlan(PlanOptions opt)
    {
        var roots = _library.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Select(l => l.TrimEnd('/'))
            .ToHashSet(StringComparer.Ordinal);
        var ctx = new Ctx();
        var plans = new List<MoviePlan>();

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

            var root = roots.Where(r => dir == r || dir.StartsWith(r + "/", StringComparison.Ordinal))
                .OrderByDescending(r => r.Length).FirstOrDefault() ?? string.Empty;
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
                    _logger.LogWarning(ex, "Movie Curator: could not plan {Path}", movie.Path);
                }
            }
        }

        return plans;
    }

    private static void Move(MoviePlan p, Ctx ctx, string src, string dst, string kind, string? note = null)
    {
        if (src == dst)
        {
            return;
        }

        var caseOnly = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase);
        if (ctx.IsTaken(dst) && !caseOnly)
        {
            p.Warnings.Add($"Skipped, target already exists: {dst}");
            return;
        }

        ctx.Taken.Add(dst);
        p.Ops.Add(new PlanOp { Kind = kind, From = src, To = dst, Note = note });
    }

    private MoviePlan PlanMovie(Movie m, string dir, List<string> siblings, string layout, PlanOptions opt, Ctx ctx)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(m.Path);
        var names = NamesFor(m, layout == "single" ? System.IO.Path.GetFileName(dir) : stem);
        var title = names.Folder;
        var source = names.Source;
        var ids = m.ProviderIds.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var p = new MoviePlan
        {
            ItemId = m.Id.ToString("N"),
            Path = m.Path,
            JellyfinName = m.Name,
            OriginalTitle = m.OriginalTitle,
            Year = m.ProductionYear,
            ProviderIds = ids,
            Title = title,
            FileName = names.File,
            TitleSource = source,
            Layout = layout,
            Suspicious = !(ids.ContainsKey("Tmdb") || ids.ContainsKey("Imdb")) || AnimeProviders.Any(ids.ContainsKey),
            Locked = m.IsLocked,
        };
        (p.GuessName, p.GuessYear) = layout == "single"
            ? GuessFromName(stem, System.IO.Path.GetFileName(dir))
            : GuessFromName(stem);

        // 0. display title shown in Jellyfin
        if (names.Display != null && names.Display != m.Name)
        {
            p.DisplayName = names.Display;
            p.Ops.Add(new PlanOp { Kind = "display", From = m.Name ?? string.Empty, To = names.Display, ItemId = p.ItemId });
        }

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

                if (!Directory.Exists(target) && ctx.Taken.Add(target))
                {
                    p.Ops.Add(new PlanOp { Kind = "mkdir", From = string.Empty, To = target });
                }
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

        var finalVideo = System.IO.Path.Combine(final, newStem + System.IO.Path.GetExtension(m.Path));
        p.FinalPath = finalVideo;

        // 5. NFO so the identification lives next to the file
        if (opt.Nfo)
        {
            var hasNfo = File.Exists(System.IO.Path.Combine(dir, stem + ".nfo"))
                || (layout == "single" && siblings.Any(f => f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)));
            if (!(ids.ContainsKey("Tmdb") || ids.ContainsKey("Imdb")))
            {
                p.Warnings.Add("No NFO: not identified yet (use Identify first)");
            }
            else if (!hasNfo)
            {
                p.Ops.Add(new PlanOp
                {
                    Kind = "nfo",
                    To = System.IO.Path.Combine(final, newStem + ".nfo"),
                    Note = opt.LockNfo ? "locked" : null,
                    Content = NfoXml(m, names.Display ?? m.Name ?? string.Empty, ids, opt.LockNfo),
                });
            }
        }

        return p;
    }

    private static void PlanOrphanSubs(MoviePlan p, Ctx ctx, string dir, string target, List<string> handled, string title, int? movieYear)
    {
        var subs = Directory.GetFiles(dir)
            .Where(f => SubExt.Contains(System.IO.Path.GetExtension(f)) && !handled.Contains(f) && !IsJunk(f))
            .ToList();
        foreach (var sub in Directory.GetDirectories(dir).Where(d => !IsJunk(d) && !HasVideosBelow(d)))
        {
            subs.AddRange(Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories)
                .Where(f => SubExt.Contains(System.IO.Path.GetExtension(f)) && !IsJunk(f)));
        }

        var year = movieYear?.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    private static string NfoXml(Movie m, string title, Dictionary<string, string> ids, bool lockData)
    {
        var movie = new XElement("movie", new XElement("title", title));
        if (!string.IsNullOrEmpty(m.OriginalTitle))
        {
            movie.Add(new XElement("originaltitle", m.OriginalTitle));
        }

        if (m.ProductionYear is int y)
        {
            movie.Add(new XElement("year", y));
        }

        if (!string.IsNullOrEmpty(m.Overview))
        {
            movie.Add(new XElement("plot", m.Overview));
        }

        if (lockData)
        {
            movie.Add(new XElement("lockdata", "true"));
        }

        if (ids.TryGetValue("Tmdb", out var tmdb))
        {
            movie.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), tmdb));
            movie.Add(new XElement("tmdbid", tmdb));
        }

        if (ids.TryGetValue("Imdb", out var imdb))
        {
            movie.Add(new XElement("uniqueid", new XAttribute("type", "imdb"), imdb));
            movie.Add(new XElement("imdbid", imdb));
        }

        if (ids.TryGetValue("TmdbCollection", out var coll))
        {
            movie.Add(new XElement("tmdbcollectionid", coll));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), movie).Declaration + "\n" + movie + "\n";
    }

    // ------------------------------------------------------------------ lock
    public async Task<int> SetLocked(IEnumerable<string> itemIds, bool locked, CancellationToken ct)
    {
        var n = 0;
        foreach (var id in itemIds)
        {
            if (!Guid.TryParse(id, out var guid) || _library.GetItemById(guid) is not { } item || item.IsLocked == locked)
            {
                continue;
            }

            item.IsLocked = locked;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            n++;
        }

        _logger.LogInformation("Movie Curator: {Action} {Count} item(s)", locked ? "locked" : "unlocked", n);
        return n;
    }

    // ------------------------------------------------------------------ apply / undo
    private static void Rename(string src, string dst, bool isDir)
    {
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) && src != dst)
        {
            var tmp = src + ".organize_tmp"; // case-only rename on exFAT
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
            throw new InvalidOperationException("Movie no longer in the library (it may have been re-added after a move).");
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
                    _logger.LogError(ex, "Movie Curator: {Kind} failed for {From}", op.Kind, op.From);
                }
            }
        }

        if (log.Count > 0)
        {
            Directory.CreateDirectory(LogDir);
            var path = System.IO.Path.Combine(LogDir, $"apply-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }));
            result.Log = path;
            _logger.LogInformation("Movie Curator: applied {Count} changes, log {Log}", log.Count, path);
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
                    case "mkdir":
                        if (!Directory.EnumerateFileSystemEntries(op.To).Any()) Directory.Delete(op.To);
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
                _logger.LogError(ex, "Movie Curator: library scan failed");
            }
        });
    }
}
