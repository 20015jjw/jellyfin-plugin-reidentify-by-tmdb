#!/usr/bin/env bash
# Builds the plugin, publishes it as a GitHub release, and adds the version to manifest.json
# so Jellyfin servers that have this repository added can install or update to it.
#
#   ./release.sh 1.2.0.0 "What changed"
#
# Needs: dotnet SDK, gh (logged in), python3, zip. Run from a clean, committed working tree.
set -euo pipefail
cd "$(dirname "$0")"

die() { echo "release.sh: $*" >&2; exit 1; }

version="${1:-}"
changelog="${2:-}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "usage: ./release.sh <version like 1.2.0.0> \"<changelog>\""
[[ -n "$changelog" ]] || die "give a changelog as the second argument"

# The Jellyfin version the plugin is built against; a server older than this won't offer the release.
jellyfin_version="${JELLYFIN_VERSION:-12.4.0}"
target_abi="${jellyfin_version}.0"

dll="Jellyfin.Plugin.ReidentifyByTmdb.dll"
tag="v${version}"
zip_name="reidentify-by-tmdb_${version}.zip"

[[ -z "$(git status --porcelain)" ]] || die "commit or stash your changes first"
git rev-parse -q --verify "refs/tags/${tag}" >/dev/null && die "tag ${tag} already exists"
repo="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
gh release view "$tag" >/dev/null 2>&1 && die "release ${tag} already exists on GitHub"

dotnet build -c Release -p:PluginVersion="$version" -p:JellyfinVersion="$jellyfin_version" --nologo -v quiet

rm -rf dist && mkdir dist
cp "bin/Release/net10.0/${dll}" dist/
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# meta.json inside the zip (what the server reads at load) and the new entry in manifest.json
# (what the server reads from the repository) are generated together so they can't disagree.
VERSION="$version" CHANGELOG="$changelog" TARGET_ABI="$target_abi" TIMESTAMP="$timestamp" DLL="$dll" \
python3 - <<'PY'
import json, os
e = os.environ
pkg = json.load(open("manifest.json"))[0]
meta = {
    "category": pkg["category"], "changelog": e["CHANGELOG"], "description": pkg["description"],
    "guid": pkg["guid"], "name": pkg["name"], "overview": pkg["overview"], "owner": pkg["owner"],
    "targetAbi": e["TARGET_ABI"], "timestamp": e["TIMESTAMP"], "version": e["VERSION"],
    "status": "Active", "autoUpdate": True, "assemblies": [e["DLL"]],
}
json.dump(meta, open("dist/meta.json", "w"), indent=2)
PY

(cd dist && zip -q -X "$zip_name" "$dll" meta.json)
checksum="$(md5 -q "dist/${zip_name}" 2>/dev/null || md5sum "dist/${zip_name}" | cut -d' ' -f1)"

VERSION="$version" CHANGELOG="$changelog" TARGET_ABI="$target_abi" TIMESTAMP="$timestamp" CHECKSUM="$checksum" \
SOURCE_URL="https://github.com/${repo}/releases/download/${tag}/${zip_name}" \
python3 - <<'PY'
import json, os
e = os.environ
manifest = json.load(open("manifest.json"))
versions = manifest[0]["versions"]
assert all(v["version"] != e["VERSION"] for v in versions), "version already in manifest.json"
# Newest first.
versions.insert(0, {
    "version": e["VERSION"], "changelog": e["CHANGELOG"], "targetAbi": e["TARGET_ABI"],
    "sourceUrl": e["SOURCE_URL"], "checksum": e["CHECKSUM"], "timestamp": e["TIMESTAMP"],
})
with open("manifest.json", "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
PY

# Upload the zip first (tagging the commit it was built from), then publish the manifest that points at it.
git push -q origin HEAD
gh release create "$tag" "dist/${zip_name}" --target "$(git rev-parse HEAD)" --title "$version" --notes "$changelog"
git add manifest.json
git commit -q -m "Release ${version}"
git push -q origin HEAD

echo
echo "Released ${version} (md5 ${checksum})."
echo "Jellyfin picks it up from the catalog, or on its next 'Update Plugins' run; restart Jellyfin to load it."
