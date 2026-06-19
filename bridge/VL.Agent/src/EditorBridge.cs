using System.Text.Json;
using VL.HDE;

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
                .Select(o => o?.ToString() ?? "<null>")
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

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = true };

    private record EditorSnapshot(DocSnapshot[] Documents, string[] Selection, MsgSnapshot[] CompilerMessages);
    private record DocSnapshot(string? Path, string? Name, bool IsChanged, bool IsReadOnly);
    private record MsgSnapshot(string Severity, string? What, string? Why);
}
