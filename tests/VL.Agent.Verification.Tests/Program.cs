using VL.Agent;

static CompilerDiagnostic Error(string what)
    => new("Error", what, "");

static CompilerVerificationObservation Settle(
    CompilerVerificationTracker tracker,
    long revision,
    params CompilerDiagnostic[] diagnostics)
{
    var first = tracker.Observe(revision, diagnostics);
    Assert(first.Status == CompilerVerificationStatus.Pending, "first stable frame must remain pending");
    return tracker.Observe(revision, diagnostics);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

{
    var tracker = new CompilerVerificationTracker(
        [Error("existing"), new("Warning", "ignored", "")],
        baselineRevision: 10,
        settleFrames: 2,
        timeoutFrames: 5);
    var result = Settle(tracker, 11, Error("existing"), new("WARNING", "also ignored", ""));
    Assert(result.Status == CompilerVerificationStatus.Passed, "unchanged baseline errors should pass");
    Assert(result.NewErrors.Length == 0, "warnings must not be reported as new errors");
}

{
    var tracker = new CompilerVerificationTracker([], 4, 2, 5);
    var result = Settle(tracker, 5, Error("new"));
    Assert(result.Status == CompilerVerificationStatus.Failed, "stable new error should fail");
    Assert(result.NewErrors is [{ What: "new" }], "new error should be returned");
}

{
    var tracker = new CompilerVerificationTracker([Error("duplicate")], 7, 2, 5);
    var result = Settle(tracker, 8, Error("duplicate"), Error("duplicate"));
    Assert(result.Status == CompilerVerificationStatus.Failed, "duplicate multiplicity must be preserved");
    Assert(result.NewErrors.Length == 1, "only the additional duplicate should be new");
}

{
    var tracker = new CompilerVerificationTracker([], 2, 2, 3);
    Assert(tracker.Observe(2, []).Status == CompilerVerificationStatus.Pending, "frame one should be pending");
    Assert(tracker.Observe(2, []).Status == CompilerVerificationStatus.Pending, "frame two should be pending");
    var result = tracker.Observe(2, []);
    Assert(result.Status == CompilerVerificationStatus.TimedOut, "missing compiler emission should time out");
    Assert(!result.ObservedCompilerEmission, "timeout should report that no emission was observed");
}

{
    var tracker = new CompilerVerificationTracker([], 20, 2, 6);
    Assert(tracker.Observe(21, [Error("transient")]).Status == CompilerVerificationStatus.Pending, "transient frame should be pending");
    Assert(tracker.Observe(21, []).Status == CompilerVerificationStatus.Pending, "changed snapshot resets stability");
    var result = tracker.Observe(21, []);
    Assert(result.Status == CompilerVerificationStatus.Passed, "disappeared transient error should pass after settling");
}

{
    var source = new FakeMessageSource();
    VL.HDE.API.LatestMessagesFromCompiler = source;
    var baseline = EditorMessages.CompilerSnapshot();
    source.Emit([new FakeMessage("Error", "emitted")]);
    var emitted = EditorMessages.CompilerSnapshot();
    Assert(emitted.Revision > baseline.Revision, "compiler observable emission must advance revision");
    Assert(emitted.Diagnostics is [{ What: "emitted" }], "emitted diagnostics should be captured");

    VL.HDE.API.LatestMessagesFromCompiler = new FakeMessageSource();
    var replaced = EditorMessages.CompilerSnapshot();
    Assert(replaced.Revision > emitted.Revision, "compiler source replacement must keep revision monotonic");
}

Console.WriteLine("compiler verification tracker: ok");

internal sealed record FakeMessage(string Severity, string What, string Why = "");

internal sealed class FakeMessageSource : IObservable<object[]>
{
    private IObserver<object[]>? _observer;

    public object[] Value { get; private set; } = [];

    public IDisposable Subscribe(IObserver<object[]> observer)
    {
        _observer = observer;
        observer.OnNext(Value);
        return EmptyDisposable.Instance;
    }

    public void Emit(object[] messages)
    {
        Value = messages;
        _observer?.OnNext(messages);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
