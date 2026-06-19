using VL.Core.Import;
using VL.HDE;

namespace VL.Agent;

/// <summary>
/// Continuously mirrors live editor state to a file for an external coding agent.
/// Drop one of these into a running patch (ideally an <c>.HDE.vl</c> editor extension)
/// and point <c>path</c> at a well-known location the agent reads — it rewrites the
/// snapshot only when the editor state actually changes (selection / messages /
/// loaded documents), so it is cheap to leave running.
/// </summary>
[ProcessNode(Name = "EditorWatcher")]
public class EditorWatcher
{
    private int _lastSignature;
    private string? _lastPath;

    /// <summary>
    /// Writes a fresh snapshot to <paramref name="path"/> whenever editor state changes.
    /// </summary>
    /// <param name="status">Last action: idle / unchanged / the write result.</param>
    /// <param name="path">Output file the agent reads (e.g. an MCP-configured path).</param>
    /// <param name="enabled">Set false to pause mirroring.</param>
    public void Update(out string status, string path = "", bool enabled = true)
    {
        if (!enabled || string.IsNullOrWhiteSpace(path)) { status = "idle"; return; }

        var signature = ComputeSignature();
        if (signature == _lastSignature && path == _lastPath) { status = "unchanged"; return; }

        _lastSignature = signature;
        _lastPath = path;
        status = EditorBridge.WriteEditorSnapshot(path);
    }

    // Cheap change-detection: hash selection identities, document paths/dirty-flags,
    // and the compiler-message count. No LINQ to keep per-frame allocation minimal.
    private static int ComputeSignature()
    {
        var hc = new HashCode();

        var selection = API.CurrentSelection?.Value;
        if (selection is not null)
            foreach (var o in selection) hc.Add(o?.GetHashCode() ?? 0);

        var documents = API.LoadedDocuments;
        if (documents is not null)
            foreach (var d in documents.Values) { hc.Add(d.FilePath); hc.Add(d.IsChanged); }

        var messages = API.LatestMessagesFromCompiler?.Value;
        if (messages is not null) hc.Add(messages.Count);

        return hc.ToHashCode();
    }
}
