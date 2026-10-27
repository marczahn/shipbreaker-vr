#!/usr/bin/env bash
# Builds the plugin (warnings count as errors) and runs its tests. Run it before every pull request.
#
# Usage: tools/check.sh [dotnet properties]
# Takes the same folder properties as the build, passed on to dotnet, for example
#   tools/check.sh -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Hardspace Shipbreaker"
#   tools/check.sh -p:ManagedDir=<Shipbreaker_Data/Managed> -p:BepInExDir=<BepInEx/core> -p:XrDir=<Unity.XR.*.dll folder>
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$ROOT/src/ShipbreakerVR" -c Release -nologo -v q -warnaserror "$@"
dotnet test "$ROOT/tests/ShipbreakerVR.Tests" -c Release -nologo -v q "$@"
