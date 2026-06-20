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

echo "== mcp tools/list contains context query tool =="
printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | "$DOTNET" tools/vl-mcp/bin/Release/net10.0/vl-mcp.dll \
  | grep -q 'vvvv_context_query'

echo "== mcp resources/list contains graph transaction schema =="
printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"resources/list","params":{}}' \
  | "$DOTNET" tools/vl-mcp/bin/Release/net10.0/vl-mcp.dll \
  | grep -q 'agentic-vl://schema/graph-transaction'

echo "== mcp resources/read returns graph transaction schema =="
printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"agentic-vl://schema/graph-transaction"}}' \
  | "$DOTNET" tools/vl-mcp/bin/Release/net10.0/vl-mcp.dll \
  | grep -q 'agentic-vl GraphTransaction'

echo "== mcp context query reports missing snapshot cleanly =="
printf '%s\n' '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"vvvv_context_query","arguments":{"kind":"summary"}}}' \
  | "$DOTNET" tools/vl-mcp/bin/Release/net10.0/vl-mcp.dll \
  | grep -q 'No editor snapshot'

echo "== mcp context query summarizes a snapshot =="
TMP_STATE="$(mktemp)"
trap 'rm -f "$TMP_STATE"' EXIT
cat > "$TMP_STATE" <<'JSON'
{
  "Documents": [
    { "Path": "/project/main.vl", "Name": "main.vl", "IsChanged": false, "IsReadOnly": false }
  ],
  "Selection": [
    { "Type": "VL.Lang.PublicAPI.ILiveElement", "Name": "IOBox", "UniqueId": "abc:def", "Kind": "Node" }
  ],
  "CompilerMessages": [
    { "Severity": "Warning", "What": "Example", "Why": "Smoke test" }
  ]
}
JSON
printf '%s\n' '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"vvvv_context_query","arguments":{"kind":"summary"}}}' \
  | VVVV_AGENT_STATE="$TMP_STATE" "$DOTNET" tools/vl-mcp/bin/Release/net10.0/vl-mcp.dll \
  | grep -q 'abc:def'

echo "ok"
