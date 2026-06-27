using System.Text.Json;
using System.Drawing;
using System.Collections;
using System.Reflection;
using VL.Core;
using VL.Core.Import;
using VL.HDE;
using VL.Lang.Platforms;
using VL.Lang.Symbols;
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
public partial class CommandProcessor
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
            var result = ProcessFile(file, resultsDir);
            if (result.ImmediateResult is not null)
            {
                var written = WriteResult(resultsDir, Path.GetFileName(file), result.ImmediateResult, result.Trace);
                _lastResult = written;
                lastResult = written;
            }
            TryDelete(file);
            _applied++;
            appliedThisFrame++;
        }

        status = appliedThisFrame > 0 ? $"applied {appliedThisFrame} (total {_applied})" : $"waiting (applied {_applied})";
    }

    private string _lastResult = "";

}
