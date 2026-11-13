#!/usr/bin/env python3
"""Release helper used by .github/workflows/release.yml.

  update_manifest.py meta <version>                               -> prints meta.json for the plugin zip
  update_manifest.py manifest <version> <zip> <download-url> [tag] -> adds the version to manifest.json

manifest.json is the plugin repository file Jellyfin reads:
  Dashboard -> Plugins -> Repositories -> +  ->  https://raw.githubusercontent.com/<owner>/<repo>/main/manifest.json
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


def manifest(version: str, zip_path: str, url: str, tag: str | None = None) -> None:
    path = ROOT / "manifest.json"
    data = json.loads(path.read_text(encoding="utf-8")) if path.exists() and path.read_text().strip() else []
    entry = next((p for p in data if p.get("guid") == META["guid"]), None)
    if entry is None:
        entry = {"guid": META["guid"], "versions": []}
        data.append(entry)
    entry.update({k: META[k] for k in ("name", "description", "overview", "owner", "category")})
    entry["versions"] = [v for v in entry.get("versions", []) if v.get("version") != version]
    entry["versions"].insert(0, {
        "version": version,
        "changelog": changelog(tag),
        "targetAbi": META["targetAbi"],
        "sourceUrl": url,
        "checksum": hashlib.md5(pathlib.Path(zip_path).read_bytes()).hexdigest(),
        "timestamp": NOW,
    })
    entry["versions"] = entry["versions"][:KEEP_VERSIONS]
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"manifest.json: {META['name']} {version} -> {url}")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "meta":
        meta(sys.argv[2])
    elif len(sys.argv) >= 5 and sys.argv[1] == "manifest":
        manifest(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5] if len(sys.argv) > 5 else None)
    else:
        sys.exit(__doc__)
