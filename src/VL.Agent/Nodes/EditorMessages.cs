using System.Collections;
using System.Reflection;
using VL.HDE;

namespace VL.Agent;

internal static class EditorMessages
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Cache> Caches = [];
    private static long _compilerRevision;

    public static IReadOnlyList<object> LatestCompiler()
        => LatestWithRevision("compiler", API.LatestMessagesFromCompiler).Messages;

    public static IReadOnlyList<object> LatestFromAllRuntimes()
        => LatestWithRevision("runtimes", API.LatestMessagesFromAllRuntimes).Messages;

    public static (long Revision, CompilerDiagnostic[] Diagnostics) CompilerSnapshot()
    {
        var snapshot = LatestWithRevision("compiler", API.LatestMessagesFromCompiler);
        var diagnostics = snapshot.Messages
            .Select(message => new CompilerDiagnostic(
                MessageSeverity(message),
                MessageWhat(message) ?? "",
                MessageWhy(message) ?? ""))
            .ToArray();
        return (snapshot.Revision, diagnostics);
    }

    public static string MessageSeverity(object message)
        => (ReadMember(message, "Severity") ?? ReadMember(message, "Type"))?.ToString() ?? "";

    public static string? MessageWhat(object message)
        => (ReadMember(message, "What") ?? ReadMember(message, "Text") ?? ReadMember(message, "Message"))?.ToString();

    public static string? MessageWhy(object message)
        => ReadMember(message, "Why")?.ToString();

    private static (IReadOnlyList<object> Messages, long Revision) LatestWithRevision(
        string key,
        object? source)
    {
        if (source is null) return ([], 0);

        var value = ReadMember(source, "Value");
        lock (Sync)
        {
            if (!Caches.TryGetValue(key, out var cache) || !ReferenceEquals(cache.Source, source))
            {
                cache?.Dispose();
                if (key == "compiler")
                    Interlocked.Increment(ref _compilerRevision);
                cache = Subscribe(
                    source,
                    key == "compiler"
                        ? () => Interlocked.Increment(ref _compilerRevision)
                        : null);
                Caches[key] = cache;
            }

            return (
                value is not null ? ToObjects(value) : cache.Observer.Latest,
                key == "compiler"
                    ? Volatile.Read(ref _compilerRevision)
                    : cache.Observer.Revision);
        }
    }

    private static Cache Subscribe(object source, Action? onNext)
    {
        var observableType = source.GetType()
            .GetInterfaces()
            .Append(source.GetType())
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IObservable<>));

        if (observableType is null)
            return new Cache(source, EmptyObserver.Instance, null);

        var elementType = observableType.GetGenericArguments()[0];
        var observer = (ILatestObserver)Activator.CreateInstance(
            typeof(LatestObserver<>).MakeGenericType(elementType),
            [onNext])!;
        var subscription = (IDisposable?)observableType.GetMethod(nameof(IObservable<object>.Subscribe))!
            .Invoke(source, [observer]);

        return new Cache(source, observer, subscription);
    }

    private static object? ReadMember(object source, string name)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            var type = source.GetType();
            return type.GetProperty(name, flags)?.GetValue(source)
                ?? type.GetField(name, flags)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static object[] ToObjects(object? value)
    {
        if (value is null) return [];
        if (value is string) return [value];
        return value is IEnumerable items ? items.Cast<object>().ToArray() : [value];
    }

    private sealed record Cache(object Source, ILatestObserver Observer, IDisposable? Subscription) : IDisposable
    {
        public void Dispose() => Subscription?.Dispose();
    }

    private interface ILatestObserver
    {
        IReadOnlyList<object> Latest { get; }
        long Revision { get; }
    }

    private sealed class LatestObserver<T> : IObserver<T>, ILatestObserver
    {
        private readonly Action? _onNext;
        private object[] _latest = [];
        private long _revision;

        public LatestObserver(Action? onNext) => _onNext = onNext;

        public IReadOnlyList<object> Latest => _latest;
        public long Revision => Volatile.Read(ref _revision);

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value)
        {
            _latest = ToObjects(value);
            Interlocked.Increment(ref _revision);
            _onNext?.Invoke();
        }
    }

    private sealed class EmptyObserver : ILatestObserver
    {
        public static readonly EmptyObserver Instance = new();
        public IReadOnlyList<object> Latest => [];
        public long Revision => 0;
    }
}
