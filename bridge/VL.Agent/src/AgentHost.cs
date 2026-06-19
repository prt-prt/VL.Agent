using VL.Core;
using VL.Core.Import;
using VL.HDE;

namespace VL.Agent;

/// <summary>
/// Editor-extension friendly host for the agent bridge.
/// Drop this into an .HDE.vl extension instead of into the project patch being
/// edited. It keeps the agent control plane in the editor runtime so user-patch
/// crashes, pauses, and runtime exceptions do not stop request processing.
/// </summary>
[ProcessNode(Name = "AgentHost")]
public class AgentHost
{
    private readonly EditorWatcher _watcher;
    private readonly CommandProcessor _commands;

    public AgentHost(NodeContext context)
    {
        _watcher = new EditorWatcher(context);
        _commands = new CommandProcessor(context);
    }

    public void Update(
        out string agentDir,
        out string statePath,
        out string watcherStatus,
        out string commandStatus,
        out string lastResult,
        string path = "",
        bool enabled = true)
    {
        agentDir = string.IsNullOrWhiteSpace(path) ? ResolveProjectAgentDir() ?? "" : path;
        statePath = string.IsNullOrWhiteSpace(agentDir) ? "" : Path.Combine(agentDir, AgentConvention.StateFileName);

        if (string.IsNullOrWhiteSpace(agentDir))
        {
            watcherStatus = "could not resolve user project dir";
            commandStatus = "could not resolve user project dir";
            lastResult = "";
            return;
        }

        _watcher.Update(out _, out watcherStatus, statePath, enabled);
        _commands.Update(out commandStatus, out lastResult, agentDir, enabled);
    }

    private static string? ResolveProjectAgentDir()
    {
        try
        {
            var doc = API.LoadedDocuments?.Values
                .Where(d => !string.IsNullOrWhiteSpace(d.FilePath))
                .Where(d => !d.FilePath.EndsWith(".HDE.vl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.IsChanged)
                .FirstOrDefault();

            var dir = doc is null ? null : Path.GetDirectoryName(doc.FilePath);
            return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, AgentConvention.DirName);
        }
        catch
        {
            return null;
        }
    }
}
