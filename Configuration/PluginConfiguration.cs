using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MovieCurator.Configuration;

/// <summary>Saved in &lt;config&gt;/plugins/configurations/Jellyfin.Plugin.MovieCurator.xml.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---------------------------------------------------------------- movies
    /// <summary>
    /// Movie folder template. Tokens: {title} (Jellyfin's display title), {originalTitle}
    /// (falls back to {title}), {year}, {tmdb}, {imdb}. Examples:
    /// "{originalTitle} ({year})", "{title} - {year}", "{year} - {title}", "{title} ({year}) [imdbid-{imdb}]".
    /// </summary>
    public string FolderTemplate { get; set; } = "{originalTitle} ({year})";

    /// <summary>Movie video/subtitle/artwork file name template. Empty = same as the folder name.</summary>
    public string FileTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Movie title shown in Jellyfin. Empty = leave Jellyfin's title alone. When set, it is written to the
    /// NFO and to the movie on Apply (with the Name field locked).
    /// </summary>
    public string DisplayTemplate { get; set; } = string.Empty;

    // ---------------------------------------------------------------- series
    /// <summary>Series folder template. Same tokens as movies plus {tvdb}.</summary>
    public string SeriesFolderTemplate { get; set; } = "{originalTitle}";

    /// <summary>
    /// Season folder template. Tokens: {season} ({season:00} = zero-padded), {seasonTitle} (empty when Jellyfin only
    /// has "Season N"), plus the series tokens. Empty = no season folders (episodes directly in the series folder).
    /// </summary>
    public string SeasonFolderTemplate { get; set; } = "Season {season} - {seasonTitle}";

    /// <summary>
    /// Episode file name template. Tokens: {series} (the series folder name), {title}/{originalTitle} (series titles),
    /// {season}, {episode}, {episodeEnd}, {episodes} ("E05" or "E05-E06"), {episodeTitle}, {seasonTitle}.
    /// A ":" is written as the look-alike "：" because ":" isn't allowed in file names.
    /// </summary>
    public string EpisodeTemplate { get; set; } = "{season}:{title} - {episode:00}";

    /// <summary>Series title shown in Jellyfin. Empty = unchanged.</summary>
    public string SeriesDisplayTemplate { get; set; } = string.Empty;

    // ---------------------------------------------------------------- shared
    /// <summary>Regex find/replace rules applied, in order, to every generated name.</summary>
    public List<RegexRule> CleanupRules { get; set; } = new();

    /// <summary>Names typed by hand in the UI. Key is "tmdb:22", "tv-tvdb:81189", "imdb:tt..." or "path:/media/...".</summary>
    public List<NameOverride> Overrides { get; set; } = new();
}

public class RegexRule
{
    public string Pattern { get; set; } = string.Empty;

    public string Replacement { get; set; } = string.Empty;
}

public class NameOverride
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
