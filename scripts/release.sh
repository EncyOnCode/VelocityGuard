#!/usr/bin/env bash
#
# Cut a VelocityGuard release from this machine.
#
# This is the local stand-in for .github/workflows/release.yml, which cannot run while the
# GitHub account is billing-locked. It derives the three manifest fields that have broken
# releases before — version, download URL and checksum — from the tag being built, so they
# cannot drift apart:
#
#   ae8bd7b  DownloadUrl pointed at the wrong release
#   26b7301  SHA256 was uppercase; OTD compares case-sensitively
#
# Usage:  scripts/release.sh 2.0.0 [--dry-run] [--yes]

set -euo pipefail

REPO_SLUG="EncyOnCode/VelocityGuard"
ZIP_NAME="VelocityGuard.zip"
DLL_NAME="VelocityGuard.dll"

cd "$(dirname "$0")/.."
ROOT="$PWD"

# ── Arguments ────────────────────────────────────────────────────────────────
VERSION="${1:-}"
DRY_RUN=0
ASSUME_YES=0
shift || true
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --yes|-y)  ASSUME_YES=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

die() { echo "error: $*" >&2; exit 1; }

[[ -n "$VERSION" ]] || die "usage: scripts/release.sh <version> [--dry-run] [--yes]"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "version must be N.N.N (got '$VERSION')"

TAG="$VERSION"
DOWNLOAD_URL="https://github.com/${REPO_SLUG}/releases/download/${TAG}/${ZIP_NAME}"

# ── Preflight ────────────────────────────────────────────────────────────────
command -v dotnet >/dev/null || die "dotnet not found"
command -v gh     >/dev/null || die "gh not found"
command -v python3 >/dev/null || die "python3 not found"

if [[ -n "$(git status --porcelain)" ]]; then
  die "working tree is dirty; commit or stash before releasing"
fi

if git rev-parse "$TAG" >/dev/null 2>&1; then
  die "tag $TAG already exists"
fi

# The manifest path encodes the supported driver version, so find it rather than hardcode it.
# Avoids mapfile, which macOS's stock bash 3.2 does not have.
MANIFEST_LIST=$(find Repository -mindepth 4 -maxdepth 4 -name '*.json' | sort)
MANIFEST_COUNT=$(printf '%s\n' "$MANIFEST_LIST" | grep -c . || true)
[[ "$MANIFEST_COUNT" -eq 1 ]] || die "expected exactly one manifest, found: $MANIFEST_LIST"
MANIFEST=$(printf '%s\n' "$MANIFEST_LIST" | head -1)

# ── Test and build ───────────────────────────────────────────────────────────
echo "==> Running filter invariants"
# The test host targets net8.0; roll forward so it runs on whatever runtime is installed.
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/VelocityGuard.Tests/VelocityGuard.Tests.csproj -c Release

echo "==> Building plugin"
rm -rf bin obj
dotnet build VelocityGuard.csproj -c Release

[[ -f "bin/Release/$DLL_NAME" ]] || die "bin/Release/$DLL_NAME was not produced"
if [[ -f bin/Release/OpenTabletDriver.Plugin.dll ]]; then
  die "OpenTabletDriver.Plugin.dll leaked into the build output; the daemon supplies it at runtime"
fi

# ── Package ──────────────────────────────────────────────────────────────────
echo "==> Packaging"
rm -f "$ZIP_NAME"
( cd bin/Release && zip -j "$ROOT/$ZIP_NAME" "$DLL_NAME" >/dev/null )

# macOS has shasum, Linux has sha256sum. Lowercase either way: OTD's comparison is case-sensitive.
if command -v sha256sum >/dev/null; then
  SHA=$(sha256sum "$ZIP_NAME" | cut -d' ' -f1)
else
  SHA=$(shasum -a 256 "$ZIP_NAME" | cut -d' ' -f1)
fi
SHA=$(printf '%s' "$SHA" | tr '[:upper:]' '[:lower:]')

echo
echo "  version      $VERSION"
echo "  tag          $TAG"
echo "  manifest     $MANIFEST"
echo "  zip contents $(unzip -Z1 "$ZIP_NAME" | tr '\n' ' ')"
echo "  sha256       $SHA"
echo "  download     $DOWNLOAD_URL"
echo

if [[ $DRY_RUN -eq 1 ]]; then
  echo "==> Dry run: manifest not written, nothing committed, tagged or published."
  rm -f "$ZIP_NAME"
  exit 0
fi

if [[ $ASSUME_YES -eq 0 ]]; then
  read -r -p "Publish this release? [y/N] " reply
  [[ "$reply" =~ ^[Yy]$ ]] || { echo "aborted"; rm -f "$ZIP_NAME"; exit 1; }
fi

# ── Manifest ─────────────────────────────────────────────────────────────────
echo "==> Updating $MANIFEST"
MANIFEST="$MANIFEST" VERSION="$VERSION" DOWNLOAD_URL="$DOWNLOAD_URL" SHA="$SHA" python3 - <<'PY'
import json, os, pathlib

path = pathlib.Path(os.environ['MANIFEST'])
manifest = json.loads(path.read_text())
manifest['PluginVersion'] = os.environ['VERSION']
manifest['DownloadUrl'] = os.environ['DOWNLOAD_URL']
manifest['SHA256'] = os.environ['SHA']
path.write_text(json.dumps(manifest, indent=4) + '\n')
PY
git --no-pager diff --stat -- "$MANIFEST"

# ── Publish ──────────────────────────────────────────────────────────────────
echo "==> Committing, tagging and pushing"
git add "$MANIFEST"
git commit -m "release: $VERSION"
git tag "$TAG"
git push origin HEAD
git push origin "$TAG"

echo "==> Creating GitHub release"
gh release create "$TAG" "$ZIP_NAME" --title "$TAG" --generate-notes

# ── Verify what actually landed ──────────────────────────────────────────────
echo "==> Verifying published asset"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$DOWNLOAD_URL" -o "$TMP/$ZIP_NAME"
if command -v sha256sum >/dev/null; then
  REMOTE_SHA=$(sha256sum "$TMP/$ZIP_NAME" | cut -d' ' -f1)
else
  REMOTE_SHA=$(shasum -a 256 "$TMP/$ZIP_NAME" | cut -d' ' -f1)
fi
REMOTE_SHA=$(printf '%s' "$REMOTE_SHA" | tr '[:upper:]' '[:lower:]')

[[ "$REMOTE_SHA" == "$SHA" ]] || die "published asset hashes $REMOTE_SHA but the manifest claims $SHA"

rm -f "$ZIP_NAME"
echo
echo "Released $VERSION — download URL resolves and its checksum matches the manifest."
