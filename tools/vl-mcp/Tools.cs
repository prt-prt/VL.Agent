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
}
