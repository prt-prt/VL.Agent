using VL.Core.Import;

// Keep the node browser surface explicit. Public protocol and implementation
// helpers must not become accidental nodes.
[assembly: ImportType(typeof(VL.Agent.AgentHost), Category = "Agent")]
[assembly: ImportType(typeof(VL.Agent.CommandProcessor), Category = "Agent")]
[assembly: ImportType(typeof(VL.Agent.EditorBridge), Category = "Agent")]
[assembly: ImportType(typeof(VL.Agent.EditorWatcher), Category = "Agent")]
