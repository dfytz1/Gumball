#!/usr/bin/env bash
# Rhino / Grasshopper Yak package (Rhino 8, .NET 8 Grasshopper host).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

proj_ver="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' GumballGH.vbproj | head -1)"
man_ver="$(grep '^version:' packaging/manifest.yml | awk '{print $2}' | tr -d '\"')"
if [[ "$proj_ver" != "$man_ver" ]]; then
  echo "Error: GumballGH.vbproj Version ($proj_ver) must equal packaging/manifest.yml version ($man_ver)" >&2
  exit 1
fi

dotnet build "$ROOT/GumballGH.vbproj" -c Release

stage="$ROOT/packaging/stage/GhGumball"
rm -rf "$stage"
mkdir -p "$stage/net8.0"

cp "$ROOT/bin/Release/net8.0/GumballGH.gha" "$stage/net8.0/"
cp "$ROOT/Resources/GumballIcon.png" "$stage/icon.png"
cp "$ROOT/packaging/manifest.yml" "$stage/manifest.yml"

YAK="${YAK:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"
cd "$stage"
"$YAK" build --platform any

echo >&2 ""
echo >&2 "Yak artefact:"
ls -la "$stage"/*.yak >&2 || true
