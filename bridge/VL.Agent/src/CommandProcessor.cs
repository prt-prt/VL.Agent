using System.Text.Json;
using VL.Core;
using VL.Core.Import;
using VL.Lang.PublicAPI;
using VL.Model;

namespace VL.Agent;

/// <summary>
/// Applies edit requests dropped by an external coding agent. Watches
/// <c>&lt;project&gt;/.agent/requests/*.json</c>, applies each via the undo-integrated
/// <see cref="ISolution"/> API on the main loop, and writes a matching result to
/// <c>&lt;project&gt;/.agent/results/</c>.
/// <para>
/// v1 supports the <c>setPinValue</c> op:
/// <c>{ "op":"setPinValue", "uniqueId":"…", "pin":"…", "value":42, "type":"Int32" }</c>.
/// </para>
/// Runs on the editor's main thread (vvvv calls Update each frame), which is required
/// for solution mutations.
/// </summary>
[ProcessNode(Name = "CommandProcessor")]
public class CommandProcessor
{
    private readonly NodeContext _context;
    private int _applied;

    public CommandProcessor(NodeContext context) => _context = context;

    /// <param name="status">Summary: idle / waiting / "applied N (total M)".</param>
    /// <param name="lastResult">The most recent result line (ok / error).</param>
    /// <param name="path">Override the .agent directory. Empty = &lt;project&gt;/.agent.</param>
    /// <param name="enabled">Set false to stop processing requests.</param>
    public void Update(out string status, out string lastResult, string path = "", bool enabled = true)
    {
        lastResult = _lastResult;
        if (!enabled) { status = "paused"; return; }

        var agentDir = string.IsNullOrWhiteSpace(path) ? ResolveAgentDir() : path;
        if (string.IsNullOrWhiteSpace(agentDir)) { status = "could not resolve project dir — set path"; return; }

        var requestsDir = Path.Combine(agentDir, "requests");
        if (!Directory.Exists(requestsDir)) { status = $"waiting (applied {_applied})"; return; }

        var resultsDir = Path.Combine(agentDir, "results");
        Directory.CreateDirectory(resultsDir);

        string[] files;
        try { files = Directory.GetFiles(requestsDir, "*.json"); }
        catch { files = []; }
        Array.Sort(files, StringComparer.Ordinal);

        int appliedThisFrame = 0;
        foreach (var file in files)
        {
            var result = ProcessFile(file);
            _lastResult = result;
            lastResult = result;
            WriteResult(resultsDir, Path.GetFileName(file), result);
            TryDelete(file);
            _applied++;
            appliedThisFrame++;
        }

        status = appliedThisFrame > 0 ? $"applied {appliedThisFrame} (total {_applied})" : $"waiting (applied {_applied})";
    }

    private string _lastResult = "";

    private string ProcessFile(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var op = GetString(root, "op") ?? "setPinValue";

            return op switch
            {
                "setPinValue" => SetPinValue(root),
                _ => Err($"unknown op '{op}'"),
            };
        }
        catch (Exception ex)
        {
            return Err($"{Path.GetFileName(file)}: {ex.Message}");
        }
    }

    private static string SetPinValue(JsonElement root)
    {
        var uidStr = GetString(root, "uniqueId");
        var pin = GetString(root, "pin");
        if (string.IsNullOrWhiteSpace(uidStr)) return Err("missing 'uniqueId'");
        if (string.IsNullOrWhiteSpace(pin)) return Err("missing 'pin'");
        if (!UniqueId.TryParse(uidStr, out var uid)) return Err($"unparseable uniqueId '{uidStr}'");
        if (!root.TryGetProperty("value", out var valueEl)) return Err("missing 'value'");

        var typeHint = GetString(root, "type");
        var value = Coerce(valueEl, typeHint);
        if (value is null) return Err($"could not coerce value for type '{typeHint}'");

        var solution = SessionNodes.CurrentSolution;
        if (solution is null) return Err("no current solution");

        solution.SetPinValue(uid, pin!, value).Confirm(SolutionUpdateKind.Default);
        return Ok($"set {pin}={value} on {uidStr}");
    }

    /// <summary>Coerce a JSON value to the CLR type vvvv expects for the pin.</summary>
    private static object? Coerce(JsonElement v, string? type)
    {
        switch ((type ?? "").ToLowerInvariant())
        {
            case "int32" or "integer32" or "int" or "integer": return v.TryGetInt32(out var i) ? i : (int)v.GetDouble();
            case "int64" or "integer64" or "long": return v.GetInt64();
            case "float32" or "float" or "single": return (float)v.GetDouble();
            case "float64" or "double": return v.GetDouble();
            case "boolean" or "bool": return v.GetBoolean();
            case "string": return v.GetString();
        }

        // No hint: infer from the JSON token.
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : v.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => v.GetBoolean(),
            JsonValueKind.String => v.GetString(),
            _ => v.ToString(),
        };
    }

    private string? ResolveAgentDir()
    {
        try
        {
            var stack = _context.Path.Stack;
            if (stack.IsEmpty) return null;
            var docPath = _context.AppHost.GetDocumentPath(stack.Peek());
            if (string.IsNullOrEmpty(docPath)) return null;
            var dir = Path.GetDirectoryName(docPath);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, AgentConvention.DirName);
        }
        catch { return null; }
    }

    private static void WriteResult(string resultsDir, string requestFileName, string result)
    {
        try
        {
            var path = Path.Combine(resultsDir, requestFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, result);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string Ok(string msg) => "{\"ok\":true,\"message\":" + JsonSerializer.Serialize(msg) + "}";
    private static string Err(string msg) => "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(msg) + "}";
}
