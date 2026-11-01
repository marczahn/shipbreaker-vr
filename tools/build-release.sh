#!/usr/bin/env bash
# Builds the release ZIP: BepInEx + OpenXR runtime files + the plugin + licenses, laid out like the game folder.
#
# Usage: tools/build-release.sh <version> <out-dir>
# Inputs (required environment variables):
#   MANAGED_DIR  game's Shipbreaker_Data/Managed (reference assemblies for the build)
#   BEPINEX_DIR  unpacked BepInEx 5.4.23.3 (winhttp.dll, doorstop_config.ini, .doorstop_version, BepInEx/core)
#   DEPLOY_DIR   output of the Unity helper project: plugin/, native/, subsys/, bundles/
set -euo pipefail

VERSION="${1:?version, e.g. 0.7.0}"
OUT="${2:?output directory}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANAGED_DIR="${MANAGED_DIR:?path to Shipbreaker_Data/Managed}"
BEPINEX_DIR="${BEPINEX_DIR:?path to the unpacked BepInEx 5.4.23.3}"
DEPLOY_DIR="${DEPLOY_DIR:?path to the Unity helper project output}"

grep -q "Version = \"$VERSION\"" "$ROOT/src/ShipbreakerVR/Plugin.cs" \
  || { echo "Plugin.cs does not declare version $VERSION" >&2; exit 1; }

STAGE="$(mktemp -d)"
BUILD="$(mktemp -d)"
dotnet build "$ROOT/src/ShipbreakerVR" -c Release -nologo -v q \
  -p:ManagedDir="$MANAGED_DIR" -p:BepInExDir="$BEPINEX_DIR/BepInEx/core" -p:XrDir="$DEPLOY_DIR/plugin" -o "$BUILD"

PLUGIN="$STAGE/BepInEx/plugins/ShipbreakerVR"
mkdir -p "$STAGE/BepInEx/core" "$PLUGIN/AssetBundles" "$PLUGIN/licenses" \
  "$STAGE/Shipbreaker_Data/Plugins/x86_64" "$STAGE/Shipbreaker_Data/UnitySubsystems/UnityOpenXR"

cp "$BEPINEX_DIR/winhttp.dll" "$BEPINEX_DIR/doorstop_config.ini" "$BEPINEX_DIR/.doorstop_version" "$STAGE/"
cp "$BEPINEX_DIR"/BepInEx/core/* "$STAGE/BepInEx/core/"
cp "$DEPLOY_DIR"/native/UnityOpenXR.dll "$DEPLOY_DIR"/native/openxr_loader.dll "$STAGE/Shipbreaker_Data/Plugins/x86_64/"
cp "$DEPLOY_DIR/subsys/UnitySubsystemsManifest.json" "$STAGE/Shipbreaker_Data/UnitySubsystems/UnityOpenXR/"
find "$DEPLOY_DIR/plugin" -name 'Unity.*.dll' -exec cp {} "$PLUGIN/" \;
cp "$DEPLOY_DIR/bundles/xrmanager" "$DEPLOY_DIR/bundles/xrmanager.manifest" "$PLUGIN/AssetBundles/"
cp "$BUILD/ShipbreakerVR.dll" "$PLUGIN/"
cp "$ROOT"/licenses/* "$PLUGIN/licenses/"
cp "$ROOT/LICENSE" "$ROOT/DISCLAIMER.md" "$PLUGIN/licenses/"
cp "$ROOT/docs/INSTALL.md" "$ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/DISCLAIMER.md" "$STAGE/"
mkdir -p "$STAGE/images" && cp "$ROOT"/docs/images/* "$STAGE/images/"  # INSTALL.md links its images relative to itself

# No game files in the release (copyright, see CLAUDE.md "No game files"): stop if any staged file is byte-identical
# to a file of the game's Shipbreaker_Data folder. Only files of the same size are hashed, so this stays fast.
GAME_DATA="$(cd "$MANAGED_DIR/.." && pwd)"
SIZES="$(find "$STAGE" -type f -printf '%s\n' | sort -u)"
CANDIDATES="$(find "$GAME_DATA" -type f -printf '%s\t%p\n' | awk -F'\t' 'NR==FNR { s[$1]; next } ($1 in s) { print $2 }' <(echo "$SIZES") -)"
if [ -n "$CANDIDATES" ]; then
  GAME_SUMS="$(echo "$CANDIDATES" | tr '\n' '\0' | xargs -0 sha256sum | cut -d' ' -f1 | sort -u)"
  FOUND="$(cd "$STAGE" && find . -type f -print0 | xargs -0 sha256sum | grep -F -f <(echo "$GAME_SUMS") || true)"
  if [ -n "$FOUND" ]; then
    echo "Release contains files from the game, refusing to build the ZIP:" >&2
    echo "$FOUND" >&2
    rm -rf "$STAGE" "$BUILD"
    exit 1
  fi
fi

mkdir -p "$OUT"
ZIP="$(cd "$OUT" && pwd)/ShipbreakerVR-$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -q -r -X "$ZIP" . )
rm -rf "$STAGE" "$BUILD"
echo "$ZIP"
