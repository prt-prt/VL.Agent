namespace VL.Agent;

/// <summary>
/// Shared path convention for the vvvv-agent. The bridge writes live editor state
/// to <c>&lt;projectDir&gt;/.agent/editor-state.json</c>; the MCP server reads the same
/// relative location from its working directory. Keeping it convention-based means
/// neither the vvvv node nor the MCP config needs a hand-set path.
/// </summary>
public static class AgentConvention
{
    public const string DirName = ".agent";
    public const string StateFileName = "editor-state.json";

    /// <summary>The default snapshot path inside a project directory.</summary>
    public static string StatePathFor(string projectDir)
        => Path.Combine(projectDir, DirName, StateFileName);
}
