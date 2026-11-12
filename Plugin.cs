using Jellyfin.Plugin.MovieCurator.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MovieCurator;

/// <summary>Movie Curator: puts every movie in "&lt;Original title&gt; (&lt;Year&gt;)/" and writes locked NFOs.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Movie Curator";

    public override Guid Id => Guid.Parse("7c3f2a6e-5d1b-4e8f-9a40-2b6d8e1f0c93");

    public override string Description =>
        "Preview and apply a clean '<Original title> (<Year>)' folder layout for movies, with locked NFO files.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MovieCurator",
                DisplayName = "Movie Curator",
                EmbeddedResourcePath = GetType().Namespace + ".Web.curator.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "drive_file_move",
            },
        };
    }
}
