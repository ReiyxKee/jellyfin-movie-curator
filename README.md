# Jellyfin Movie Curator

A Jellyfin 10.11 plugin that tidies your **movie** libraries from a page in the Dashboard.

For every movie it previews, and on your OK applies:

- its own folder named **`<Original title> (<Year>)`**, taken from Jellyfin's metadata or a name you type
- the video, its subtitles and artwork renamed to match (`Movie (2016).mkv`, `Movie (2016).en.srt`, `Movie (2016)-poster.jpg`)
- stray subtitles (`Venom [ENG].srt`, `Subs/English SDH.eng.srt`) renamed to `<name>.<lang>.srt` so players find them
- movies that share a folder (e.g. `Pokemon Movies/`) moved into a subfolder each
- a **locked `.nfo`** with the TMDB/IMDb IDs, so a manual *Identify* survives refreshes, renames and rescans

Movies that look wrongly matched (no TMDB/IMDb ID, or matched by an anime provider) are flagged,
and you can fix them right on the page (search & pick, or suggested matches) and lock them against batch refreshes.

Library locations come from Jellyfin itself, so it works with whatever paths your server uses.

## Safety

- No files change until you select movies and press **Apply folder changes**.
- It never deletes media and never overwrites an existing file; conflicts are shown and skipped.
- Every apply is logged in the plugin's data folder; **Undo last apply** reverses it (and removes NFOs it created).
- A library scan starts automatically after apply/undo.
- Moving files changes their paths, so Jellyfin re-adds them as new items. The NFO keeps the identification, but **watched status may reset**.

## Install

### From the plugin repository (recommended)

1. Jellyfin → **Dashboard → Plugins → Repositories → +**, and pick a channel:

   | Channel | Name | URL |
   | --- | --- | --- |
   | **Stable** | `Movie Curator` | `https://raw.githubusercontent.com/ReiyxKee/jellyfin-movie-curator/main/manifest.json` |
   | **Experimental** (stable + test builds) | `Movie Curator (experimental)` | `https://raw.githubusercontent.com/ReiyxKee/jellyfin-movie-curator/main/manifest-experimental.json` |

   Add only one of them. Switching from experimental back to stable takes effect with the next stable release.
2. **Catalog → Movie Curator → Install**, then restart Jellyfin.
3. Updates show up in Jellyfin when a new version is released (or install automatically with auto-update on).

If you installed it by hand before, delete that folder from `<jellyfin config>/plugins/` first.

### Manually


Build it (see below), copy `dist/MovieCurator_<version>/` into `<jellyfin config>/plugins/`, then restart Jellyfin.

## Use

Dashboard → **Movie Curator**. The page does three jobs:

**1. Group movies into folders.** Select movies and press **Apply folder changes**.

| Option | Effect |
| --- | --- |
| Rename folders & files | Rename existing folders, videos, subs and artwork to your naming template. Off = only move loose movies into folders. |
| Write NFO | Write `<name>.nfo` next to the video (existing NFOs are kept). |
| Lock NFO | Add `<lockdata>true</lockdata>` so the identification survives moves and rescans. |

**2. Fix wrong matches.**
- **Fix match…** on a movie: search by title (+ year) or by ID (`tt0325980`, `tmdb:22`) and pick a result. This uses Jellyfin's own Identify.
- **Suggest matches**: searches TMDB for every selected (or flagged) movie using its file name; confident matches are highlighted green. You still click **Use this** on each.
- With **Lock movies after fixing a match** on, a fixed movie is locked straight away.

**3. Lock movies against batch updates.** **Lock / Unlock** per movie, or **Lock selected**. Locked movies are skipped by scheduled metadata refreshes and "Refresh all metadata". Fixing a match on a locked movie unlocks it first, applies, then locks it again.

### Naming

Open **Naming** at the top of the page.

| Template | Default | Examples |
| --- | --- | --- |
| Folder | `{originalTitle} ({year})` | `{title} - {year}`, `{year} - {title}`, `{title} ({year}) [imdbid-{imdb}]` |
| File | same as folder | `{title}` |
| Display title | unchanged | `{originalTitle}` to show native titles in Jellyfin |

Tokens: `{title}`, `{originalTitle}` (falls back to `{title}`), `{year}`, `{tmdb}`, `{imdb}`. Empty brackets and dangling dashes are removed when a value is missing.

**Regex clean-up rules** run in order on every generated name, one per line as `pattern => replacement` (.NET regex, `$1` for groups). For example:

```
^劇場版\s* =>
\s*：\s* => -
```

A display-title change shows in the preview as a 🏷 change. On Apply it renames the movie in Jellyfin and locks its Name field; undo restores the old title.

You can also type a custom name on any movie (Enter to save); **Reset name** goes back to the template.

## Series

Switch to the **Series** tab. Default layout (all templates editable under **Naming**):

```
Nanatsu no Taizai/
  Season 1 - <season title>/
    1：The Seven Deadly Sins - 01.mkv
  Season 2 - <season title>/
    2：The Seven Deadly Sins - 01.mkv
```

- **Series level** (always): folder name, fix match (Jellyfin's series search), locked `tvshow.nfo`, and
  **Lock (with episodes)** to lock the series together with all its seasons and episodes.
- **Episodes** (turn on *Rename episodes into season folders*): episodes move into season folders and get renamed.
  Only episodes Jellyfin has an episode number for are touched; the rest are listed and left alone.
- **Merging:** folders in the same library that Jellyfin identified as the same show (e.g. `Grand Blue`,
  `Grand Blue Season 2`, `Grand Blue Season 3`) are shown as one card and merged into one folder, each as its own season.
- **Season numbers:** Jellyfin's season is used when it's 0 (specials) or 2+. When it says 1 or nothing — common for
  anime kept one folder per season — the folder's own name wins (`Season 2`, `S2`, `2nd Season`, `第2期`, `第二季`, `II`),
  or a number you type on the card.
- **Safety:** if two season folders would end up with the same season number, or two files share a season+episode
  number, that show is marked *season numbering conflict* and its episodes are not renamed. Fix the match or set the
  season, then refresh.
- Empty folders left behind are removed; undo recreates them.

## Build

Requires the .NET 9 SDK.

```bash
./build.sh            # -> dist/MovieCurator_<version>/
sudo ./build.sh       # no local SDK: builds inside the mcr.microsoft.com/dotnet/sdk:9.0 container
```

## Project layout

| Path | What |
| --- | --- |
| `Plugin.cs` | Plugin entry; registers the Dashboard page |
| `Curator/CuratorService.cs` | Planning, apply and undo logic |
| `Api/CuratorController.cs` | Admin-only API: `GET /MovieCurator/Plan`, `POST Apply`, `POST Undo`, `POST Lock`, `POST Name` |
| `Web/curator.html` | Dashboard page |
| `Configuration/PluginConfiguration.cs` | Saved naming templates, regex rules and custom names |
| `build.sh` | Builds and stages the plugin into `dist/` |
| `meta.json` | Plugin manifest copied next to the DLL |

## License

Copyright (C) 2026 Reiyx

Movie Curator is free software: you can redistribute it and/or modify it under the terms of the
**GNU General Public License v3.0** as published by the Free Software Foundation. See [`LICENSE`](LICENSE).

This plugin is, and will always be, free for everyone. It is a non-commercial community project;
please don't sell it. Any copy or modified version you share must stay open source under the same license.

## Releasing (maintainers)

```bash
git tag v1.2.0 && git push origin v1.2.0              # stable       -> 1.2.0.0, both manifests
git tag v1.2.0-exp.1 && git push origin v1.2.0-exp.1  # experimental -> 1.2.0.1, experimental manifest only
```

Number experimental builds after the stable release they build on (`v<last stable>-exp.N`), so the next stable
release (`1.3.0` = 1.3.0.0) is higher than all of them and experimental users move onto it.

GitHub Actions builds the plugin, publishes a GitHub Release (a pre-release for experimental) with the zip and updates
the channel manifest(s)
(`.github/workflows/release.yml`). Every push to `main` is also compile-checked (`build.yml`). Versions must increase.

