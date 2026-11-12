using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MovieCurator.Configuration;

/// <summary>Saved in &lt;config&gt;/plugins/configurations/Jellyfin.Plugin.MovieCurator.xml.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Folder name template. Tokens: {title} (Jellyfin's display title), {originalTitle}
    /// (falls back to {title}), {year}, {tmdb}, {imdb}. Examples:
    /// "{originalTitle} ({year})", "{title} - {year}", "{year} - {title}", "{title} ({year}) [imdbid-{imdb}]".
    /// </summary>
    public string FolderTemplate { get; set; } = "{originalTitle} ({year})";

    /// <summary>Video/subtitle/artwork file name template. Empty = same as the folder name.</summary>
    public string FileTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Title shown in Jellyfin. Empty = leave Jellyfin's title alone. When set, it is written to the
    /// NFO and to the movie on Apply (with the Name field locked).
    /// </summary>
    public string DisplayTemplate { get; set; } = string.Empty;

    /// <summary>Regex find/replace rules applied, in order, to every generated name.</summary>
    public List<RegexRule> CleanupRules { get; set; } = new();

    /// <summary>Names typed by hand in the UI. Key is "tmdb:22", "imdb:tt..." or "path:/media/movies/...".</summary>
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
