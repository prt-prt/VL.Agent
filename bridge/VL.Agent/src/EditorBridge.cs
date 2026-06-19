using System.Reflection;
using System.Text.Json;
using VL.HDE;
using VL.Lang.PublicAPI;

namespace VL.Agent;

/// <summary>
/// Bridge nodes that export live vvvv editor state for an external coding agent.
///
/// <see cref="VL.HDE.API"/> turns out to be a <b>static</b> class, so editor state
/// (loaded documents, selection, compiler messages) is reachable globally — a node
/// needs no wiring to obtain it. These nodes simply read it and write a snapshot.
/// </summary>
public static class EditorBridge
{
    /// <summary>
    /// Serializes a snapshot of the current editor state — loaded documents,
    /// current selection, and the latest compiler messages — to <paramref name="path"/>
    /// as JSON. Returns a short status line. Intended to be triggered from a patch
    /// (e.g. on a bang) so an external coding agent can read live editor state.
    /// </summary>
    public static string WriteEditorSnapshot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "no output path";

        try
        {
            var documents = API.LoadedDocuments?.Values
                .Select(d => new DocSnapshot(d.FilePath, d.FileName, d.IsChanged, d.IsReadOnly))
                .ToArray() ?? [];

            var selection = API.CurrentSelection?.Value?
                .Select(DescribeSelected)
                .ToArray() ?? [];

            var messages = API.LatestMessagesFromCompiler?.Value?
                .Select(m => new MsgSnapshot(m.Severity.ToString(), m.What, m.Why))
                .ToArray() ?? [];

            var snapshot = new EditorSnapshot(documents, selection, messages);

            var json = JsonSerializer.Serialize(snapshot, SnapshotJson);
            File.WriteAllText(path, json);

            return $"ok: {documents.Length} docs, {selection.Length} selected, "
                 + $"{messages.Length} compiler messages -> {path}";
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    /// <summary>
    /// Turn one selected object into structured data. Selection comes through as
    /// <c>Spread&lt;object&gt;</c>; when an entry is an <see cref="ILiveElement"/> we
    /// pull its id/name/symbol/messages, otherwise we fall back to its text. The
    /// runtime <c>Type</c> is always recorded so the snapshot is self-documenting.
    /// </summary>
    private static SelSnapshot DescribeSelected(object? o)
    {
        if (o is null) return new SelSnapshot("null", null, null, null, null, null);
        var type = o.GetType().FullName ?? o.GetType().Name;

        if (o is ILiveElement live)
        {
            var info = live.Info;
            var messages = live.Messages?.Select(m => m.ToString() ?? "").ToArray();
            return new SelSnapshot(type, info?.ElementID, info?.ElementName,
                info?.SymbolInfoString, info?.IsUnused, messages);
        }

        // Unknown selection object: emit a self-diagnosing dump (interfaces + readable
        // properties) so we can see exactly how to extract its id/name. Also make a
        // best effort to surface an id from a property named like an id.
        var interfaces = o.GetType().GetInterfaces().Select(i => i.Name).OrderBy(n => n).ToArray();
        var props = ReadProperties(o);
        var id = props.TryGetValue("ElementID", out var ev) ? ev
               : props.TryGetValue("Id", out var iv) ? iv
               : props.GetValueOrDefault("Identity");
        var name = props.GetValueOrDefault("Name") ?? o.ToString();

        return new SelSnapshot(type, null, name, null, null, null, interfaces, props, id);
    }

    /// <summary>Reflectively read public instance properties, guarded and truncated.</summary>
    private static Dictionary<string, string?> ReadProperties(object o)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            try
            {
                var v = p.GetValue(o)?.ToString();
                if (v is { Length: > 200 }) v = v[..200] + "…";
                result[p.Name] = v;
            }
            catch (Exception ex) { result[p.Name] = "<err: " + ex.GetType().Name + ">"; }
        }
        return result;
    }

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private record EditorSnapshot(DocSnapshot[] Documents, SelSnapshot[] Selection, MsgSnapshot[] CompilerMessages);
    private record DocSnapshot(string? Path, string? Name, bool IsChanged, bool IsReadOnly);
    private record SelSnapshot(
        string Type, uint? Id, string? Name, string? Symbol, bool? IsUnused, string[]? Messages,
        string[]? Interfaces = null, Dictionary<string, string?>? Properties = null, string? ProbedId = null);
    private record MsgSnapshot(string Severity, string? What, string? Why);
}
