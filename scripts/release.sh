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

# Without this, any failing command exits silently with no indication of where it stopped —
# which is exactly how a release can look like it "just did nothing".
trap 'echo "error: release.sh aborted at line $LINENO" >&2' ERR

REPO_SLUG="EncyOnCode/VelocityGuard"
ZIP_NAME="VelocityGuard.zip"
DLL_NAME="VelocityGuard.dll"

cd "$(dirname "$0")/.."
ROOT="$PWD"

# ── Arguments ────────────────────────────────────────────────────────────────
VERSION="${1:-}"
DRY_RUN=0
ASSUME_YES=0
ALLOW_BRANCH=0
shift || true
for arg in "$@"; do
  case "$arg" in
    --dry-run)      DRY_RUN=1 ;;
    --yes|-y)       ASSUME_YES=1 ;;
    --allow-branch) ALLOW_BRANCH=1 ;;
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

# The OTD plugin manager reads the manifest from the repository's default branch (source ref
# "main"). Releasing from anywhere else publishes the zip but leaves the manifest users actually
# see pointing at the previous version — the release looks fine and delivers nothing.
DEFAULT_BRANCH=$(gh repo view "$REPO_SLUG" --json defaultBranchRef --jq .defaultBranchRef.name)
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
if [[ "$CURRENT_BRANCH" != "$DEFAULT_BRANCH" ]]; then
  if [[ $ALLOW_BRANCH -eq 0 ]]; then
    die "on '$CURRENT_BRANCH', but OTD reads the manifest from '$DEFAULT_BRANCH'.
       Merge first, or pass --allow-branch if you really mean to tag this branch."
  fi
  echo "warning: releasing from '$CURRENT_BRANCH' rather than '$DEFAULT_BRANCH'" >&2
fi

# A stale local branch would publish a manifest that does not match what is on the remote.
git fetch --quiet origin "$CURRENT_BRANCH"
if [[ -n "$(git rev-list "origin/$CURRENT_BRANCH..HEAD" 2>/dev/null)" ]] ||
   [[ -n "$(git rev-list "HEAD..origin/$CURRENT_BRANCH" 2>/dev/null)" ]]; then
  die "'$CURRENT_BRANCH' is out of sync with origin; push or pull before releasing"
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
  # Without a terminal, `read` hits EOF and set -e would kill the script with no message at all.
  # Fail loudly and tell the caller what to do instead.
  if [[ ! -t 0 ]]; then
    rm -f "$ZIP_NAME"
    die "no terminal to confirm on; re-run with --yes to publish non-interactively"
  fi
  reply=""
  read -r -p "Publish this release? [y/N] " reply || true
  if [[ ! "$reply" =~ ^[Yy]$ ]]; then
    rm -f "$ZIP_NAME"
    echo "aborted — nothing was committed, tagged or published."
    exit 1
  fi
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
