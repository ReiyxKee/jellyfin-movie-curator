#!/usr/bin/env python3
"""Release helper used by .github/workflows/release.yml.

  update_manifest.py meta <version>                                          -> prints meta.json for the plugin zip
  update_manifest.py manifest <version> <zip> <download-url> <tag> <channel> -> adds the version to the manifest(s)

Channels (each is a plugin repository URL people add in Jellyfin: Dashboard -> Plugins -> Repositories -> +):
  stable        manifest.json               stable releases only
  experimental  manifest-experimental.json  stable + experimental releases
A stable release goes into both files, an experimental one only into manifest-experimental.json.
"""
import datetime
import hashlib
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
META = json.loads((ROOT / "meta.json").read_text(encoding="utf-8"))
NOW = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
KEEP_VERSIONS = 20  # older versions drop out of the manifest (their GitHub releases stay)


def git(*args: str) -> str:
    try:
        return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=False).stdout.strip()
    except OSError:
        return ""


def changelog(tag: str | None) -> str:
    """Commit subjects since the previous tag (bot manifest commits left out)."""
    ref = tag or "HEAD"
    prev = git("describe", "--tags", "--abbrev=0", f"{ref}^")
    log = git("log", "--pretty=%s", f"{prev}..{ref}" if prev else ref)
    lines = ["- " + l.strip().lstrip("-*• ").strip() for l in log.splitlines() if l.strip() and not l.startswith("manifest:")]
    return "\n".join(lines[:30]) or "Release"


def meta(version: str) -> None:
    print(json.dumps(dict(META, version=version, timestamp=NOW, changelog=changelog(None)), indent=2, ensure_ascii=False))


CHANNEL_FILES = {"stable": ["manifest.json", "manifest-experimental.json"], "experimental": ["manifest-experimental.json"]}


def manifest(version: str, zip_path: str, url: str, tag: str | None = None, channel: str = "stable") -> None:
    if channel not in CHANNEL_FILES:
        sys.exit(f"unknown channel {channel!r} (stable | experimental)")
    entry_version = {
        "version": version,
        "changelog": ("[experimental] " if channel == "experimental" else "") + changelog(tag),
        "targetAbi": META["targetAbi"],
        "sourceUrl": url,
        "checksum": hashlib.md5(pathlib.Path(zip_path).read_bytes()).hexdigest(),
        "timestamp": NOW,
    }
    for name in CHANNEL_FILES[channel]:
        write_manifest(ROOT / name, version, entry_version)
    print(f"{channel}: {META['name']} {version} -> {url} ({', '.join(CHANNEL_FILES[channel])})")


def write_manifest(path: pathlib.Path, version: str, version_entry: dict) -> None:
    data = json.loads(path.read_text(encoding="utf-8")) if path.exists() and path.read_text().strip() else []
    entry = next((p for p in data if p.get("guid") == META["guid"]), None)
    if entry is None:
        entry = {"guid": META["guid"], "versions": []}
        data.append(entry)
    entry.update({k: META[k] for k in ("name", "description", "overview", "owner", "category")})
    versions = [v for v in entry.get("versions", []) if v.get("version") != version] + [version_entry]
    # newest first (Jellyfin compares versions; keep the list ordered the same way)
    versions.sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
    entry["versions"] = versions[:KEEP_VERSIONS]
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "meta":
        meta(sys.argv[2])
    elif len(sys.argv) >= 5 and sys.argv[1] == "manifest":
        manifest(sys.argv[2], sys.argv[3], sys.argv[4],
                 sys.argv[5] if len(sys.argv) > 5 else None,
                 sys.argv[6] if len(sys.argv) > 6 else "stable")
    else:
        sys.exit(__doc__)
