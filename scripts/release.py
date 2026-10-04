#!/usr/bin/env python3
"""Builds one release zip per Jellyfin line and rewrites repo/manifest.json.

    python scripts/release.py --version 0.3.0 [--targets 10.10.7,10.11.11]

Each Jellyfin line needs its own binary (the plugin API and target framework differ), so the
manifest carries one entry per line. The last version component orders them: on a 10.11
server both the 10.10 and 10.11 entries are installable, and 10.11 must be the higher one.
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src/Jellyfin.Plugin.JellyTrends/Jellyfin.Plugin.JellyTrends.csproj"
OWNER, REPO = "marceljhuber", "JellyTrends"
GUID = "5e4f95f0-df85-4ef4-a73c-30afde8be5f9"
FILE_TRANSFORMATION = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90"

# Jellyfin line -> (framework, version revision, targetAbi)
LINES = {
    "10.10": ("net8.0", 0, "10.10.0.0"),
    "10.11": ("net9.0", 1, "10.11.0.0"),
    "12.0": ("net10.0", 2, "12.0.0.0"),
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True, help="plugin version without revision, e.g. 0.3.0")
    parser.add_argument("--targets", default="10.10.7,10.11.11", help="comma separated Jellyfin versions")
    parser.add_argument("--changelog", default="")
    args = parser.parse_args()

    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    versions = []

    for jellyfin in args.targets.split(","):
        line = ".".join(jellyfin.split(".")[:2])
        framework, revision, abi = LINES[line]
        version = f"{args.version}.{revision}"
        out = ROOT / "artifacts" / f"publish-{line}"
        shutil.rmtree(out, ignore_errors=True)

        print(f"== Jellyfin {jellyfin} ({framework}) -> {version}")
        subprocess.run(
            ["dotnet", "publish", str(PROJECT), "-c", "Release", "-f", framework, "-o", str(out),
             f"-p:JellyfinVersion={jellyfin}", f"-p:PluginVersion={args.version}"],
            check=True)

        zip_name = f"JellyTrends-{version}-jellyfin-{line}.zip"
        zip_path = dist / zip_name
        zip_path.unlink(missing_ok=True)
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
            # Jellyfin ships its own copies of everything else; only the plugin goes in the zip.
            archive.write(out / "Jellyfin.Plugin.JellyTrends.dll", "Jellyfin.Plugin.JellyTrends.dll")

        versions.append({
            "version": version,
            "changelog": args.changelog or f"Release {version}",
            "targetAbi": abi,
            "sourceUrl": f"https://raw.githubusercontent.com/{OWNER}/{REPO}/master/dist/{zip_name}",
            "checksum": hashlib.md5(zip_path.read_bytes()).hexdigest().upper(),
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "dependencies": [FILE_TRANSFORMATION],
        })

    # Newest first, as the catalogue expects.
    versions.sort(key=lambda v: tuple(int(p) for p in v["version"].split(".")), reverse=True)

    manifest = [{
        "guid": GUID,
        "name": "JellyTrends",
        "overview": "Netflix-style trending rows built from your own library",
        "description": (
            "Adds Top N Movies and Top N Shows rows to the Jellyfin home screen, built from the "
            "titles you own. Charts come from TMDB, Trakt or the keyless Cinemeta catalog, and each "
            "title keeps its real position in the online chart. Matching runs on the server, and the "
            "rows use Jellyfin's own cards and scrollers. Requires the File Transformation plugin."
        ),
        "owner": OWNER,
        "category": "General",
        "imageUrl": f"https://raw.githubusercontent.com/{OWNER}/{REPO}/master/assets/jellytrends-banner.png",
        "versions": versions,
    }]

    # No BOM, so any JSON reader can consume it.
    (ROOT / "repo/manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("Manifest written. Force-add the zips: git add -f dist/*.zip")
    return 0


if __name__ == "__main__":
    sys.exit(main())
