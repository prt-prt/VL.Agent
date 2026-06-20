using System.Text.Json;
using System.Text.Json.Nodes;
using VlMcp;

// MCP stdio server: read newline-delimited JSON-RPC 2.0 from stdin, write
// responses to stdout. ALL diagnostics go to stderr — stdout is the protocol
// channel and must carry nothing but JSON-RPC messages.

var stdout = Console.Out;
void Log(string msg) => Console.Error.WriteLine($"[vl-mcp] {msg}");

Log("starting (stdio)");

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonNode? msg;
    try { msg = JsonNode.Parse(line); }
    catch (Exception ex) { Log($"parse error: {ex.Message}"); continue; }
    if (msg is null) continue;

    var id = msg["id"];
    var method = (string?)msg["method"];

    // Notifications have no id and expect no response.
    if (id is null)
    {
        Log($"notification: {method}");
        continue;
    }

    JsonObject response;
    try
    {
        var result = Dispatch(method, msg["params"]);
        response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    }
    catch (RpcException rex)
    {
        response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = rex.Code, ["message"] = rex.Message },
        };
    }
    catch (Exception ex)
    {
        Log($"handler error: {ex}");
        response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "internal error: " + ex.Message },
        };
    }

    stdout.WriteLine(response.ToJsonString());
    stdout.Flush();
}

Log("stdin closed, exiting");
return 0;

JsonNode Dispatch(string? method, JsonNode? @params) => method switch
{
    "initialize" => Initialize(@params),
    "resources/list" => Resources.List(),
    "resources/read" => Resources.Read(@params),
    "tools/list" => Tools.List(),
    "tools/call" => Tools.Call(@params),
    "ping" => new JsonObject(),
    _ => throw new RpcException(-32601, $"method not found: {method}"),
};

JsonNode Initialize(JsonNode? @params)
{
    // Echo the client's protocol version when offered, for maximum compatibility.
    var protocol = (string?)@params?["protocolVersion"] ?? "2024-11-05";
    Log($"initialize (protocol {protocol})");
    return new JsonObject
    {
        ["protocolVersion"] = protocol,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
            ["resources"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject { ["name"] = "vl-mcp", ["version"] = "0.1.0" },
    };
}

namespace VlMcp
{
    /// <summary>A JSON-RPC error to return to the client.</summary>
    internal sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    }
}
