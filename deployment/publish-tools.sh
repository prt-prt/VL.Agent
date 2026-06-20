#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
RID="${RID:-win-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"
SELF_CONTAINED="${SELF_CONTAINED:-false}"
PACKAGE_TOOLS_DIR="${PACKAGE_TOOLS_DIR:-$ROOT/package/tools}"

TOOLS=(vl-map vl-mcp vl-probe)
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/agentic-vl-tools.XXXXXX")"
OUT_DIR="$PACKAGE_TOOLS_DIR/$RID"

cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

for tool in "${TOOLS[@]}"; do
  project="$ROOT/tools/$tool/$tool.csproj"
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
