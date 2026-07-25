using System.Text.Json;

namespace VL.Agent;

public partial class CommandProcessor
{
    private PendingCompilerVerification? _pendingCompilerVerification;

    private bool HasPendingCompilerVerification => _pendingCompilerVerification is not null;

    private static bool TryReadCompilerVerification(
        JsonElement transaction,
        out CompilerVerificationOptions? options,
        out string? error)
    {
        options = null;
        error = null;
        if (!transaction.TryGetProperty("verify", out var verify))
            return false;
        if (verify.ValueKind != JsonValueKind.Object)
        {
            error = "verify must be an object";
            return true;
        }

        var compiler = GetString(verify, "compiler");
        if (!string.Equals(compiler, "noNewErrors", StringComparison.Ordinal))
        {
            error = "verify.compiler must be 'noNewErrors'";
            return true;
        }

        var settleFrames = ReadFrameCount(verify, "settleFrames", 2);
        var timeoutFrames = ReadFrameCount(verify, "timeoutFrames", 120);
        if (settleFrames is < 1 or > 30)
            error = "verify.settleFrames must be between 1 and 30";
        else if (timeoutFrames < settleFrames || timeoutFrames > 600)
            error = "verify.timeoutFrames must be between settleFrames and 600";
        else
            options = new CompilerVerificationOptions(settleFrames, timeoutFrames);
        return true;
    }

    private static int ReadFrameCount(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private void QueueCompilerVerification(
        string resultsDir,
        string requestFileName,
        CommandTrace trace,
        GraphTxPlan plan,
        string label,
        CompilerVerificationOptions options,
        long baselineRevision,
        CompilerDiagnostic[] baseline)
    {
        if (_pendingCompilerVerification is not null)
            throw new InvalidOperationException("a compiler verification is already pending");

        _pendingCompilerVerification = new PendingCompilerVerification(
            resultsDir,
            requestFileName,
            trace,
            plan,
            label,
            options,
            new CompilerVerificationTracker(
                baseline,
                baselineRevision,
                options.SettleFrames,
                options.TimeoutFrames));
    }

    /// <summary>Advances at most one verification once per vvvv frame.</summary>
    private string? TickPendingCompilerVerification()
    {
        var pending = _pendingCompilerVerification;
        if (pending is null)
            return null;

        var snapshot = EditorMessages.CompilerSnapshot();
        var observation = pending.Tracker.Observe(snapshot.Revision, snapshot.Diagnostics);
        if (observation.Status == CompilerVerificationStatus.Pending)
            return null;

        var outcome = new CompilerVerificationOutcome(
            observation,
            pending.Options.SettleFrames,
            pending.Options.TimeoutFrames);
        var result = BuildTransactionResult(
            plan: pending.Plan,
            dryRun: false,
            label: pending.Label,
            shouldApply: true,
            verificationRequested: true,
            outcome: outcome);
        var written = WriteResult(
            pending.ResultsDir,
            pending.RequestFileName,
            result,
            pending.Trace);
        _pendingCompilerVerification = null;
        return written;
    }

    private static string CompilerVerificationStatusText(CompilerVerificationStatus status)
        => status switch
        {
            CompilerVerificationStatus.Passed => "passed",
            CompilerVerificationStatus.Failed => "failed",
            CompilerVerificationStatus.TimedOut => "timedOut",
            _ => "pending",
        };

    private sealed record CompilerVerificationOptions(int SettleFrames, int TimeoutFrames);

    private sealed record CompilerVerificationOutcome(
        CompilerVerificationObservation Observation,
        int SettleFrames,
        int TimeoutFrames);

    private sealed record PendingCompilerVerification(
        string ResultsDir,
        string RequestFileName,
        CommandTrace Trace,
        GraphTxPlan Plan,
        string Label,
        CompilerVerificationOptions Options,
        CompilerVerificationTracker Tracker);
}
