#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"

cd "$ROOT"

echo "== dotnet =="
"$DOTNET" --version

echo "== build vl-mcp =="
"$DOTNET" build tools/vl-mcp/vl-mcp.csproj -c Release

echo "== parse graph transaction schema and examples =="
python3 - <<'PY'
import json
from pathlib import Path

json.load(open("schemas/graph-transaction.schema.json"))
print("ok schemas/graph-transaction.schema.json")

for path in sorted(Path("examples/graph-transactions").glob("*.json")):
    json.load(open(path))
    print(f"ok {path}")
PY

echo "== mcp tools/list contains graph transaction tool =="
printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \
  | "$DOTNET" tools/vl-mcp/bin/Release/net10.0/vl-mcp.dll \
  | grep -q 'vvvv_apply_graph_transaction'

echo "ok"
