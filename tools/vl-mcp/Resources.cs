using System.Text.Json.Nodes;

namespace VlMcp;

/// <summary>
/// MCP resources for larger read-only context. Keep the tool surface small and
/// expose bulky docs/schemas/snapshots through resources that clients can read
/// on demand.
/// </summary>
internal static class Resources
{
    private static readonly ResourceDef[] StaticResources =
    [
        new(
            "agentic-vl://schema/graph-transaction",
            "GraphTransaction schema",
            "Machine-readable JSON schema for graph transaction payloads.",
            "application/schema+json",
            "schemas/graph-transaction.schema.json"),
        new(
            "agentic-vl://docs/session-context",
            "Session context",
            "Concise current project handoff and architecture state.",
            "text/markdown",
            "docs/SESSION_CONTEXT.md"),
        new(
            "agentic-vl://docs/windows-testing",
            "Windows testing checklist",
            "Running checklist for tests that need vvvv gamma on Windows.",
            "text/markdown",
            "docs/WINDOWS_TESTING.md"),
        new(
            "agentic-vl://docs/graph-transaction-protocol",
            "Graph transaction protocol",
            "Protocol notes for graph transaction implementation.",
            "text/markdown",
            "docs/graph-transaction-protocol.md"),
    ];

    public static JsonNode List()
    {
        var resources = new JsonArray();
        foreach (var r in StaticResources)
            resources.Add(ToListItem(r));

        resources.Add(new JsonObject
        {
            ["uri"] = "agentic-vl://editor/state",
            ["name"] = "Live editor snapshot",
            ["description"] = "Latest .agent/editor-state.json written by VL.Agent, if present.",
            ["mimeType"] = "application/json",
        });

        return new JsonObject { ["resources"] = resources };
    }

    public static JsonNode Read(JsonNode? @params)
    {
        var uri = (string?)@params?["uri"];
        if (string.IsNullOrWhiteSpace(uri))
            throw new RpcException(-32602, "uri is required");

        if (uri == "agentic-vl://editor/state")
            return ReadEditorState(uri);

        var resource = StaticResources.FirstOrDefault(r => r.Uri == uri);
        if (resource is null)
            throw new RpcException(-32602, $"unknown resource: {uri}");

        var path = Path.Combine(AgentPaths.Root, resource.RelativePath);
        if (!File.Exists(path))
            throw new RpcException(-32603, $"resource file not found: {resource.RelativePath}");

        return Contents(uri, resource.MimeType, File.ReadAllText(path));
    }

    private static JsonObject ToListItem(ResourceDef r) => new()
    {
        ["uri"] = r.Uri,
        ["name"] = r.Name,
        ["description"] = r.Description,
        ["mimeType"] = r.MimeType,
    };

    private static JsonNode ReadEditorState(string uri)
    {
        var path = AgentPaths.DefaultStatePath;
        if (!File.Exists(path))
            return Contents(uri, "application/json",
                $$"""{"ok":false,"error":"No editor snapshot at '{{JsonEscape(path)}}'. Is AgentHost or EditorWatcher running?"}""");

        return Contents(uri, "application/json", File.ReadAllText(path));
    }

    private static JsonNode Contents(string uri, string mimeType, string text) => new JsonObject
    {
        ["contents"] = new JsonArray
        {
            new JsonObject
            {
                ["uri"] = uri,
                ["mimeType"] = mimeType,
                ["text"] = text,
            },
        },
    };

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record ResourceDef(
        string Uri,
        string Name,
        string Description,
        string MimeType,
        string RelativePath);
}
