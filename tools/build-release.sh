#!/usr/bin/env bash
# Builds the release ZIP: BepInEx + OpenXR runtime files + the plugin + licenses, laid out like the game folder.
#
# Usage: tools/build-release.sh <version> <out-dir>
# Inputs (override with environment variables):
#   MANAGED_DIR  game's Shipbreaker_Data/Managed (reference assemblies for the build)
#   BEPINEX_DIR  unpacked BepInEx 5.4.23.3 (winhttp.dll, doorstop_config.ini, .doorstop_version, BepInEx/core)
#   DEPLOY_DIR   output of the Unity helper project: plugin/, native/, subsys/, bundles/
set -euo pipefail

VERSION="${1:?version, e.g. 0.7.0}"
OUT="${2:?output directory}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MANAGED_DIR="${MANAGED_DIR:?path to Shipbreaker_Data/Managed}"
BEPINEX_DIR="${BEPINEX_DIR:-/mnt/project-files/bepinex-5.4.23.3}"
DEPLOY_DIR="${DEPLOY_DIR:-/mnt/project-files/ShipbreakerVR/deploy}"

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
cp "$ROOT/unity/ShipbreakerVRUnity/LICENSE-ShipbreakerVr.txt" "$PLUGIN/"
cp "$ROOT/docs/INSTALL.md" "$ROOT/THIRD-PARTY-NOTICES.md" "$STAGE/"

mkdir -p "$OUT"
ZIP="$(cd "$OUT" && pwd)/ShipbreakerVR-$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -q -r -X "$ZIP" . )
rm -rf "$STAGE" "$BUILD"
echo "$ZIP"
