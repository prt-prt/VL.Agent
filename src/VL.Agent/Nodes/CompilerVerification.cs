namespace VL.Agent;

internal readonly record struct CompilerDiagnostic(string Severity, string What, string Why);

internal enum CompilerVerificationStatus
{
    Pending,
    Passed,
    Failed,
    TimedOut,
}

internal readonly record struct CompilerVerificationObservation(
    CompilerVerificationStatus Status,
    int WaitedFrames,
    bool ObservedCompilerEmission,
    int BaselineErrorCount,
    int ObservedErrorCount,
    CompilerDiagnostic[] NewErrors);

/// <summary>
/// Pure state machine for compiler-error delta verification. It requires at least one
/// compiler observable emission after construction and a stable error snapshot before
/// reporting success or failure.
/// </summary>
internal sealed class CompilerVerificationTracker
{
    private readonly Dictionary<CompilerDiagnostic, int> _baseline;
    private readonly long _baselineRevision;
    private readonly int _settleFrames;
    private readonly int _timeoutFrames;
    private Dictionary<CompilerDiagnostic, int> _lastObserved;
    private int _waitedFrames;
    private int _stableFrames;
    private bool _observedCompilerEmission;

    public CompilerVerificationTracker(
        IReadOnlyList<CompilerDiagnostic> baseline,
        long baselineRevision,
        int settleFrames,
        int timeoutFrames)
    {
        _baseline = ErrorMultiset(baseline);
        _lastObserved = new Dictionary<CompilerDiagnostic, int>(_baseline);
        _baselineRevision = baselineRevision;
        _settleFrames = Math.Clamp(settleFrames, 1, 30);
        _timeoutFrames = Math.Clamp(timeoutFrames, _settleFrames, 600);
    }

    public CompilerVerificationObservation Observe(
        long compilerRevision,
        IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        _waitedFrames++;
        var current = ErrorMultiset(diagnostics);

        if (compilerRevision > _baselineRevision)
            _observedCompilerEmission = true;

        if (_observedCompilerEmission)
        {
            if (MultisetEquals(_lastObserved, current))
                _stableFrames++;
            else
            {
                _lastObserved = current;
                _stableFrames = 1;
            }

            if (_stableFrames >= _settleFrames)
            {
                var newErrors = NewSince(_baseline, current);
                return Observation(
                    newErrors.Length == 0 ? CompilerVerificationStatus.Passed : CompilerVerificationStatus.Failed,
                    current,
                    newErrors);
            }
        }

        return _waitedFrames >= _timeoutFrames
            ? Observation(CompilerVerificationStatus.TimedOut, current, [])
            : Observation(CompilerVerificationStatus.Pending, current, []);
    }

    private CompilerVerificationObservation Observation(
        CompilerVerificationStatus status,
        IReadOnlyDictionary<CompilerDiagnostic, int> current,
        CompilerDiagnostic[] newErrors)
        => new(
            status,
            _waitedFrames,
            _observedCompilerEmission,
            Count(_baseline),
            Count(current),
            newErrors);

    private static Dictionary<CompilerDiagnostic, int> ErrorMultiset(
        IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        var result = new Dictionary<CompilerDiagnostic, int>();
        foreach (var diagnostic in diagnostics)
        {
            var normalized = Normalize(diagnostic);
            if (!IsErrorSeverity(normalized.Severity))
                continue;
            result[normalized] = result.GetValueOrDefault(normalized) + 1;
        }
        return result;
    }

    private static CompilerDiagnostic Normalize(CompilerDiagnostic diagnostic)
        => new(
            diagnostic.Severity.Trim().ToLowerInvariant(),
            diagnostic.What.Trim(),
            diagnostic.Why.Trim());

    private static bool IsErrorSeverity(string severity)
        => severity.Contains("error", StringComparison.Ordinal)
        || severity.Contains("fatal", StringComparison.Ordinal)
        || severity.Contains("critical", StringComparison.Ordinal);

    private static bool MultisetEquals(
        IReadOnlyDictionary<CompilerDiagnostic, int> left,
        IReadOnlyDictionary<CompilerDiagnostic, int> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var item in left)
            if (right.GetValueOrDefault(item.Key) != item.Value)
                return false;
        return true;
    }

    private static CompilerDiagnostic[] NewSince(
        IReadOnlyDictionary<CompilerDiagnostic, int> baseline,
        IReadOnlyDictionary<CompilerDiagnostic, int> current)
    {
        var result = new List<CompilerDiagnostic>();
        foreach (var item in current)
        {
            var count = item.Value - baseline.GetValueOrDefault(item.Key);
            for (var i = 0; i < count; i++)
                result.Add(item.Key);
        }
        return result.ToArray();
    }

    private static int Count(IReadOnlyDictionary<CompilerDiagnostic, int> items)
    {
        var count = 0;
        foreach (var item in items)
            count += item.Value;
        return count;
    }
}
