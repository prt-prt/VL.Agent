namespace VlMcp;

internal static class AgentPaths
{
    public const string AgentDir = ".agent";
    public const string StateFileName = "editor-state.json";

    public static string ProjectRoot => Directory.GetCurrentDirectory();

    public static string DistributionRoot =>
        Environment.GetEnvironmentVariable("VL_AGENT_HOME")
        ?? FindDistributionRoot()
        ?? ProjectRoot;

    public static string DefaultStatePath =>
        Environment.GetEnvironmentVariable("VVVV_AGENT_STATE")
        ?? Path.Combine(ProjectRoot, AgentDir, StateFileName);

    public static string DefaultAgentDir =>
        Path.Combine(ProjectRoot, AgentDir);

    private static string? FindDistributionRoot()
    {
        foreach (var start in new[] { ProjectRoot, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "schemas", "graph-transaction.schema.json")))
                    return current.FullName;

                current = current.Parent;
            }
        }

        return null;
    }
}
