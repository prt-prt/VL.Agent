namespace VlMcp;

internal static class AgentPaths
{
    public const string AgentDir = ".agent";
    public const string StateFileName = "editor-state.json";

    public static string Root => Directory.GetCurrentDirectory();

    public static string DefaultStatePath =>
        Environment.GetEnvironmentVariable("VVVV_AGENT_STATE")
        ?? Path.Combine(Root, AgentDir, StateFileName);

    public static string DefaultAgentDir =>
        Path.Combine(Root, AgentDir);
}
