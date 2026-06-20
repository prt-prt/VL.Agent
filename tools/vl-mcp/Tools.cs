using System.Text.Json;
using System.Text.Json.Nodes;
using VlMap;

namespace VlMcp;

/// <summary>The vvvv-agent tools exposed over MCP.</summary>
internal static class Tools
{
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
                ["name"] = "vvvv_context_query",
                ["description"] =
                    "Read a compact slice of the live vvvv editor snapshot. Prefer this over adding "
                  + "specialized read tools: use `summary`, `documents`, `selection`, or "
                  + "`compilerMessages` depending on the decision you need to make.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray { "summary", "documents", "selection", "compilerMessages", "raw" },
                            ["description"] = "Snapshot slice to return. Defaults to summary.",
                        },
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
                  + "AgentHost/CommandProcessor must be running. Prefer AgentHost in an HDE extension. "
                  + "Returns the apply result.",
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
            new JsonObject
            {
                ["name"] = "vvvv_paste",
                ["description"] =
                    "EXPERIMENTAL dev-only live paste. Drops a clipboard-style vvvv Canvas snippet "
                  + "for the vvvv-side AgentHost/CommandProcessor to paste into the active canvas using "
                  + "a deferred UI-context call. Requires experimental=true because this mutates the live editor graph.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["snippet"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Clipboard-style <Canvas ...> model snippet.",
                        },
                        ["x"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Paste X coordinate in the active canvas. Defaults to 0.",
                        },
                        ["y"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Paste Y coordinate in the active canvas. Defaults to 0.",
                        },
                        ["experimental"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Must be true to acknowledge this dev-only live graph mutation path.",
                        },
                        ["pauseRuntime"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Pause vvvv runtimes before paste. Useful for snippets that instantiate runtime-heavy nodes.",
                        },
                        ["leaveRuntimePaused"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Keep runtimes paused after paste so newly inserted nodes cannot immediately execute.",
                        },
                        ["agentDir"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional override for the .agent directory.",
                        },
                    },
                    ["required"] = new JsonArray { "snippet", "experimental" },
                },
            },
            new JsonObject
            {
                ["name"] = "vvvv_apply_graph_transaction",
                ["description"] =
                    "EXPERIMENTAL graph transaction entry point. Sends a GraphTransaction batch to "
                  + "the vvvv-side AgentHost/CommandProcessor. First slice supports dryRun, validate, "
                  + "and batching setPin operations into one undo-integrated Confirm. Structural ops "
                  + "such as addNode/connect are reported as unsupported until the safe editor mutation "
                  + "path is proven.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["transaction"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "GraphTransaction object. See schemas/graph-transaction.schema.json.",
                        },
                        ["agentDir"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional override for the .agent directory.",
                        },
                    },
                    ["required"] = new JsonArray { "transaction" },
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
            "vvvv_context_query" => ContextQuery(args),
            "vvvv_set_pin_value" => SetPinValue(args),
            "vvvv_paste" => Paste(args),
            "vvvv_apply_graph_transaction" => ApplyGraphTransaction(args),
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
        if (string.IsNullOrWhiteSpace(path)) path = AgentPaths.Root;
        if (!Directory.Exists(path))
            return $"error: project directory not found: {path}";

        var index = Indexer.Build(path);
        return JsonSerializer.Serialize(index, Json.Indented);
    }

    private static string EditorState(JsonNode? args)
    {
        var path = (string?)args?["path"];
        if (string.IsNullOrWhiteSpace(path)) path = AgentPaths.DefaultStatePath;

        if (!File.Exists(path))
            return $"No editor snapshot at '{path}'. Is the EditorWatcher node running in vvvv, "
                 + "and is its path (or $VVVV_AGENT_STATE) pointing here?";

        var info = new FileInfo(path);
        var ageSeconds = (int)(DateTime.Now - info.LastWriteTime).TotalSeconds;
        var content = File.ReadAllText(path);

        return $"// snapshot path: {path}\n// last updated: {ageSeconds}s ago\n{content}";
    }

    private static string ContextQuery(JsonNode? args)
    {
        var path = (string?)args?["path"];
        if (string.IsNullOrWhiteSpace(path)) path = AgentPaths.DefaultStatePath;

        var kind = (string?)args?["kind"] ?? "summary";
        if (kind is not ("summary" or "documents" or "selection" or "compilerMessages" or "raw"))
            throw new RpcException(-32602, "kind must be summary, documents, selection, compilerMessages, or raw");

        if (!File.Exists(path))
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "No editor snapshot. Is AgentHost or EditorWatcher running?",
                ["path"] = path,
            }.ToJsonString(Json.Indented);
        }

        JsonNode? snapshot;
        try
        {
            snapshot = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "Editor snapshot is not valid JSON.",
                ["path"] = path,
                ["detail"] = ex.Message,
            }.ToJsonString(Json.Indented);
        }

        if (snapshot is null)
            return new JsonObject { ["ok"] = false, ["error"] = "Editor snapshot is empty.", ["path"] = path }.ToJsonString(Json.Indented);

        var info = new FileInfo(path);
        var ageSeconds = (int)(DateTime.Now - info.LastWriteTime).TotalSeconds;

        if (kind == "raw")
            return new JsonObject
            {
                ["ok"] = true,
                ["path"] = path,
                ["ageSeconds"] = ageSeconds,
                ["snapshot"] = snapshot.DeepClone(),
            }.ToJsonString(Json.Indented);

        var documents = SnapshotArray(snapshot, "Documents");
        var selection = SnapshotArray(snapshot, "Selection");
        var messages = SnapshotArray(snapshot, "CompilerMessages");

        var result = new JsonObject
        {
            ["ok"] = true,
            ["path"] = path,
            ["ageSeconds"] = ageSeconds,
        };

        switch (kind)
        {
            case "summary":
                result["counts"] = new JsonObject
                {
                    ["documents"] = documents.Count,
                    ["selection"] = selection.Count,
                    ["compilerMessages"] = messages.Count,
                };
                result["selection"] = CompactSelection(selection);
                result["compilerMessages"] = CompactMessages(messages);
                break;
            case "documents":
                result["documents"] = documents.DeepClone();
                break;
            case "selection":
                result["selection"] = selection.DeepClone();
                break;
            case "compilerMessages":
                result["compilerMessages"] = messages.DeepClone();
                break;
        }

        return result.ToJsonString(Json.Indented);
    }

    // Drop a request file the in-vvvv CommandProcessor picks up, then poll for its result.
    private static string SetPinValue(JsonNode? args)
    {
        var uniqueId = (string?)args?["uniqueId"];
        var pin = (string?)args?["pin"];
        if (string.IsNullOrWhiteSpace(uniqueId)) throw new RpcException(-32602, "uniqueId is required");
        if (string.IsNullOrWhiteSpace(pin)) throw new RpcException(-32602, "pin is required");
        if (args?["value"] is not JsonNode value) throw new RpcException(-32602, "value is required");

        var request = new JsonObject
        {
            ["op"] = "setPinValue",
            ["uniqueId"] = uniqueId,
            ["pin"] = pin,
            ["value"] = value.DeepClone(),
        };
        if ((string?)args?["type"] is { } typeHint) request["type"] = typeHint;

        return SubmitRequest(request, (string?)args?["agentDir"]);
    }

    private static string Paste(JsonNode? args)
    {
        var snippet = (string?)args?["snippet"];
        if (string.IsNullOrWhiteSpace(snippet)) throw new RpcException(-32602, "snippet is required");
        if ((bool?)args?["experimental"] != true)
            throw new RpcException(-32602, "experimental=true is required for vvvv_paste");

        var request = new JsonObject
        {
            ["op"] = "paste",
            ["snippet"] = snippet,
            ["experimental"] = true,
        };
        if ((double?)args?["x"] is { } x) request["x"] = x;
        if ((double?)args?["y"] is { } y) request["y"] = y;
        if ((bool?)args?["pauseRuntime"] is { } pauseRuntime) request["pauseRuntime"] = pauseRuntime;
        if ((bool?)args?["leaveRuntimePaused"] is { } leaveRuntimePaused) request["leaveRuntimePaused"] = leaveRuntimePaused;

        return SubmitRequest(request, (string?)args?["agentDir"]);
    }

    private static string ApplyGraphTransaction(JsonNode? args)
    {
        if (args?["transaction"] is not JsonObject transaction)
            throw new RpcException(-32602, "transaction object is required");

        var request = new JsonObject
        {
            ["op"] = "graphTransaction",
            ["transaction"] = transaction.DeepClone(),
        };

        return SubmitRequest(request, (string?)args?["agentDir"]);
    }

    private static JsonArray SnapshotArray(JsonNode snapshot, string property)
    {
        if (snapshot[property] is JsonArray array)
            return array;
        return [];
    }

    private static JsonArray CompactSelection(JsonArray selection)
    {
        var result = new JsonArray();
        foreach (var item in selection)
        {
            if (item is not JsonObject obj)
                continue;

            result.Add(new JsonObject
            {
                ["type"] = ScalarString(obj, "Type"),
                ["name"] = ScalarString(obj, "Name"),
                ["uniqueId"] = ScalarString(obj, "UniqueId"),
                ["kind"] = ScalarString(obj, "Kind"),
                ["symbol"] = ScalarString(obj, "Symbol"),
                ["messages"] = obj["Messages"]?.DeepClone(),
            });
        }
        return result;
    }

    private static JsonArray CompactMessages(JsonArray messages)
    {
        var result = new JsonArray();
        foreach (var item in messages)
        {
            if (item is not JsonObject obj)
                continue;

            result.Add(new JsonObject
            {
                ["severity"] = ScalarString(obj, "Severity"),
                ["what"] = ScalarString(obj, "What"),
                ["why"] = ScalarString(obj, "Why"),
            });
        }
        return result;
    }

    private static string? ScalarString(JsonObject obj, string property) =>
        obj[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string SubmitRequest(JsonObject request, string? agentDir)
    {
        if (string.IsNullOrWhiteSpace(agentDir)) agentDir = AgentPaths.DefaultAgentDir;

        var requestsDir = Path.Combine(agentDir, "requests");
        var resultsDir = Path.Combine(agentDir, "results");
        Directory.CreateDirectory(requestsDir);

        var id = Guid.NewGuid().ToString("N");

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
             + "Is AgentHost or CommandProcessor running and pointed at this .agent dir?\"}";
    }

}
