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
        if (o is null) return new SelSnapshot("null");
        var type = o.GetType().FullName ?? o.GetType().Name;

        if (o is ILiveElement live)
        {
            var info = live.Info;

            // The stable id lives on the underlying model Element, not on IElementInfo
            // (whose numeric ElementID is null for editor selections). UniqueId.ElementId
            // is the base62 id; MergeId is the uint that ISolution.SetPinValue accepts.
            string? elementId = null, documentId = null, kind = null;
            uint? mergeId = null;
            var element = live.Element;
            if (element is not null)
            {
                var uid = element.UniqueId;
                elementId = uid.ElementId;
                documentId = uid.DocumentId;
                mergeId = element.MergeId;
                kind = element.Kind.ToString();
            }

            var messages = live.Messages?.Select(m => m.ToString() ?? "").ToArray();
            return new SelSnapshot(type, elementId, mergeId, documentId,
                info?.ElementName, info?.SymbolInfoString, kind, info?.IsUnused, messages);
        }

        // Fallback for any non-element selection object: record type + text.
        return new SelSnapshot(type, Name: o.ToString());
    }

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private record EditorSnapshot(DocSnapshot[] Documents, SelSnapshot[] Selection, MsgSnapshot[] CompilerMessages);
    private record DocSnapshot(string? Path, string? Name, bool IsChanged, bool IsReadOnly);
    private record SelSnapshot(
        string Type,
        string? ElementId = null,   // base62 id, stable within the document
        uint? MergeId = null,       // runtime numeric id (ISolution.SetPinValue overload)
        string? DocumentId = null,
        string? Name = null,
        string? Symbol = null,
        string? Kind = null,
        bool? IsUnused = null,
        string[]? Messages = null);
    private record MsgSnapshot(string Severity, string? What, string? Why);
}
