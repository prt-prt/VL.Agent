using VL.Core;
using VL.Core.Import;
using VL.HDE;
using VL.Lang.PublicAPI;

namespace VL.Agent;

/// <summary>
/// Continuously mirrors live editor state to a file for an external coding agent.
/// Drop one of these into a running patch (ideally an <c>.HDE.vl</c> editor extension)
/// — it rewrites the snapshot only when editor state actually changes (selection /
/// messages / loaded documents), so it is cheap to leave running.
/// <para>
/// By default it writes to <c>&lt;projectDir&gt;/.agent/editor-state.json</c>, where the
/// project is the directory of the patch this node lives in — matching where the MCP
/// server looks. Set the <c>path</c> pin to override.
/// </para>
/// </summary>
[ProcessNode(Name = "EditorWatcher")]
public class EditorWatcher
{
    private readonly NodeContext _context;
    private int _lastSignature;
    private string? _lastPath;

    public EditorWatcher(NodeContext context) => _context = context;

    /// <summary>
    /// Writes a fresh snapshot whenever editor state changes.
    /// </summary>
    /// <param name="resolvedPath">The path actually written to (auto-derived if path is empty).</param>
    /// <param name="status">Last action: idle / unchanged / the write result.</param>
    /// <param name="path">Override output path. Leave empty to use &lt;project&gt;/.agent/editor-state.json.</param>
    /// <param name="enabled">Set false to pause mirroring.</param>
    public void Update(out string resolvedPath, out string status, string path = "", bool enabled = true)
    {
        resolvedPath = string.IsNullOrWhiteSpace(path) ? ResolveDefaultPath() ?? "" : path;

        if (!enabled) { status = "paused"; return; }
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            status = "could not resolve project dir - set the path pin";
            return;
        }

        var signature = ComputeSignature();
        if (signature == _lastSignature && resolvedPath == _lastPath) { status = "unchanged"; return; }

        _lastSignature = signature;
        _lastPath = resolvedPath;
        status = EditorBridge.WriteEditorSnapshot(resolvedPath);
    }

    public void ForceWrite(out string resolvedPath, out string status, string path = "", bool enabled = true)
    {
        resolvedPath = string.IsNullOrWhiteSpace(path) ? ResolveDefaultPath() ?? "" : path;

        if (!enabled) { status = "paused"; return; }
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            status = "could not resolve project dir — set the path pin";
            return;
        }

        _lastSignature = ComputeSignature();
        _lastPath = resolvedPath;
        status = EditorBridge.WriteEditorSnapshot(resolvedPath);
    }

    /// <summary>
    /// Derive the default snapshot path from the document this node lives in, placing
    /// it under that project's <c>.agent</c> dir.
    /// </summary>
    private string? ResolveDefaultPath()
    {
        try
        {
            var stack = _context.Path.Stack;
            if (stack.IsEmpty) return null;
            var docPath = _context.AppHost.GetDocumentPath(stack.Peek());
            if (string.IsNullOrEmpty(docPath)) return null;
            var dir = Path.GetDirectoryName(docPath);
            return string.IsNullOrEmpty(dir) ? null : AgentConvention.StatePathFor(dir);
        }
        catch { return null; }
    }

    // Cheap change-detection: hash selection identities, document paths/dirty-flags,
    // and the compiler-message count. No LINQ to keep per-frame allocation minimal.
    private static int ComputeSignature()
    {
        var hc = new HashCode();

        var selection = API.CurrentSelection?.Value;
        if (selection is not null)
        {
            foreach (var o in selection)
            {
                hc.Add(o?.GetHashCode() ?? 0);
                AddLiveValueSignature(ref hc, o);
            }
        }

        var documents = API.LoadedDocuments;
        if (documents is not null)
            foreach (var d in documents.Values) { hc.Add(d.FilePath); hc.Add(d.IsChanged); }

        hc.Add(EditorMessages.LatestCompiler().Count);

        return hc.ToHashCode();
    }

    private static void AddLiveValueSignature(ref HashCode hc, object? item)
    {
        try
        {
            if (item is ILiveNodeApplication node)
            {
                foreach (var pin in node.Pins)
                {
                    hc.Add(pin.Info.Name);
                    hc.Add(pin.Info.Value?.ToString());
                    hc.Add(pin.IsConnected);
                }
            }
            else if (item is ILiveDataHub hub)
            {
                hc.Add(hub.Info.Value?.ToString());
                hc.Add(hub.Info.DefaultValue?.ToString());
                hc.Add(hub.IsConnected);
            }
        }
        catch { }
    }
}
