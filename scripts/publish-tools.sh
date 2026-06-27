#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
RID="${RID:-win-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"
PACKAGE_TOOLS_DIR="${PACKAGE_TOOLS_DIR:-$ROOT/artifacts/tools}"

TOOLS=(vl-map vl-mcp vl-probe)
PROJECTS=(VL.Agent.Map VL.Agent.Mcp VL.Agent.Probe)
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/vl-agent-tools.XXXXXX")"
OUT_DIR="$PACKAGE_TOOLS_DIR/$RID"

cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

for index in "${!TOOLS[@]}"; do
  tool="${TOOLS[$index]}"
  project_name="${PROJECTS[$index]}"
  project="$ROOT/src/$project_name/$project_name.csproj"
  publish_dir="$TMP_DIR/$tool"

  echo "== publish $tool ($RID) =="
  "$DOTNET" publish "$project" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained "$SELF_CONTAINED" \
    -o "$publish_dir" \
    "$@"
done

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

for tool in "${TOOLS[@]}"; do
  cp -R "$TMP_DIR/$tool/." "$OUT_DIR/"
done

echo "== staged package tools =="
find "$OUT_DIR" -maxdepth 1 -type f -print | sort
