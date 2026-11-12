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
