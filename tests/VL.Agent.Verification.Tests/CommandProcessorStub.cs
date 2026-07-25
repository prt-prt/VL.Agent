using System.Text.Json;

namespace VL.Agent;

public partial class CommandProcessor
{
    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string BuildTransactionResult(
        GraphTxPlan plan,
        bool dryRun,
        string label,
        bool shouldApply,
        bool verificationRequested = false,
        CompilerVerificationOutcome? outcome = null)
        => "";

    private static string WriteResult(
        string resultsDir,
        string requestFileName,
        string result,
        CommandTrace trace)
        => result;

    private sealed class GraphTxPlan { }
    private sealed class CommandTrace { }
}
