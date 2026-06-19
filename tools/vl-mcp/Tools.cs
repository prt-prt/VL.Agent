using System.Text.Json;
using System.Text.Json.Nodes;
using VlMap;

namespace VlMcp;

/// <summary>The vvvv-agent tools exposed over MCP.</summary>
internal static class Tools
{
    // Convention shared with the in-vvvv EditorWatcher node: <project>/.agent/editor-state.json.
    // The server's working directory is the project (Claude Code launches it there), so no
    // path config is needed. Overridable by the tool's `path` arg or $VVVV_AGENT_STATE.
    private const string AgentDir = ".agent";
    private const string StateFileName = "editor-state.json";

    private static string DefaultStatePath =>
        Environment.GetEnvironmentVariable("VVVV_AGENT_STATE")
        ?? Path.Combine(Directory.GetCurrentDirectory(), AgentDir, StateFileName);

    private static string DefaultAgentDir =>
        Path.Combine(Directory.GetCurrentDirectory(), AgentDir);

    public static JsonNode List() => new JsonObject
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "vvvv_index_project",
                ["description"] =
                    "Statically index a vvvv gamma project directory: definitions, dependencies, "
                  + "document-dependency graph, package version drift, missing document deps, and "
                  + "duplicate element IDs. Returns the full index as JSON. Read-only; runs no vvvv code.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["projectPath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Project directory to index. Defaults to the working directory.",
                        },
                    },
                },
            },
            new JsonObject
            {
                ["name"] = "vvvv_editor_state",
                ["description"] =
                    "Read the live vvvv editor snapshot written by the in-vvvv EditorWatcher node "
                  + "(loaded documents, current selection, compiler messages). Reports the file's age "
                  + "so you know how fresh it is.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional override for the snapshot file path.",
                        },
                    },
                },
            },
            new JsonObject
            {
                ["name"] = "vvvv_set_pin_value",
                ["description"] =
                    "Set an input pin's value on a node/element in the live vvvv editor, undo-integrated. "
                  + "Use the `UniqueId` of a selected element from vvvv_editor_state. The vvvv-side "
                  + "CommandProcessor node must be running in the patch. Returns the apply result.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["uniqueId"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Element UniqueId from vvvv_editor_state (the `UniqueId` field).",
                        },
                        ["pin"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Name of the input pin to set (e.g. \"Input\", \"Increment\").",
                        },
                        ["value"] = new JsonObject
                        {
                            ["description"] = "New value (number / string / boolean).",
                        },
                        ["type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional CLR type hint: Int32 / Float32 / Float64 / Boolean / String. "
                                            + "If omitted, inferred from the JSON value.",
                        },
                    },
                    ["required"] = new JsonArray { "uniqueId", "pin", "value" },
                },
            },
        },
    };

    public static JsonNode Call(JsonNode? @params)
    {
        var name = (string?)@params?["name"] ?? throw new RpcException(-32602, "missing tool name");
        var args = @params?["arguments"];

        var text = name switch
        {
            "vvvv_index_project" => IndexProject(args),
            "vvvv_editor_state" => EditorState(args),
            "vvvv_set_pin_value" => SetPinValue(args),
            _ => throw new RpcException(-32602, $"unknown tool: {name}"),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        };
    }

    private static string IndexProject(JsonNode? args)
    {
        var path = (string?)args?["projectPath"];
        if (string.IsNullOrWhiteSpace(path)) path = Directory.GetCurrentDirectory();
        if (!Directory.Exists(path))
            return $"error: project directory not found: {path}";

        var index = Indexer.Build(path);
        return JsonSerializer.Serialize(index, Json.Indented);
    }

    private static string EditorState(JsonNode? args)
    {
        var path = (string?)args?["path"];
        if (string.IsNullOrWhiteSpace(path)) path = DefaultStatePath;

        if (!File.Exists(path))
            return $"No editor snapshot at '{path}'. Is the EditorWatcher node running in vvvv, "
                 + "and is its path (or $VVVV_AGENT_STATE) pointing here?";

        var info = new FileInfo(path);
        var ageSeconds = (int)(DateTime.Now - info.LastWriteTime).TotalSeconds;
        var content = File.ReadAllText(path);

        return $"// snapshot path: {path}\n// last updated: {ageSeconds}s ago\n{content}";
    }

    // Drop a request file the in-vvvv CommandProcessor picks up, then poll for its result.
    private static string SetPinValue(JsonNode? args)
    {
        var uniqueId = (string?)args?["uniqueId"];
        var pin = (string?)args?["pin"];
        if (string.IsNullOrWhiteSpace(uniqueId)) throw new RpcException(-32602, "uniqueId is required");
        if (string.IsNullOrWhiteSpace(pin)) throw new RpcException(-32602, "pin is required");
        if (args?["value"] is not JsonNode value) throw new RpcException(-32602, "value is required");

        var agentDir = (string?)args?["agentDir"];
        if (string.IsNullOrWhiteSpace(agentDir)) agentDir = DefaultAgentDir;

        var requestsDir = Path.Combine(agentDir, "requests");
        var resultsDir = Path.Combine(agentDir, "results");
        Directory.CreateDirectory(requestsDir);

        var id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["op"] = "setPinValue",
            ["uniqueId"] = uniqueId,
            ["pin"] = pin,
            ["value"] = value.DeepClone(),
        };
        if ((string?)args?["type"] is { } typeHint) request["type"] = typeHint;

        // Write atomically (temp + rename) so the watcher never sees a partial file.
        var requestPath = Path.Combine(requestsDir, id + ".json");
        var tmp = requestPath + ".tmp";
        File.WriteAllText(tmp, request.ToJsonString());
        File.Move(tmp, requestPath, overwrite: true);

        // Poll for the matching result (~5s). The CommandProcessor applies on the vvvv main loop.
        var resultPath = Path.Combine(resultsDir, id + ".json");
        for (var i = 0; i < 50; i++)
        {
            if (File.Exists(resultPath))
            {
                string result;
                try { result = File.ReadAllText(resultPath); }
                catch { Thread.Sleep(50); continue; }
                try { File.Delete(resultPath); } catch { }
                return result;
            }
            Thread.Sleep(100);
        }

        return "{\"ok\":false,\"error\":\"timed out waiting for vvvv to apply the request. "
             + "Is the CommandProcessor node running in the patch, and pointed at this .agent dir?\"}";
    }
}
