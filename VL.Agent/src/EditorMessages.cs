using System.Collections;
using System.Reflection;
using VL.HDE;

namespace VL.Agent;

internal static class EditorMessages
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Cache> Caches = [];

    public static IReadOnlyList<object> LatestCompiler()
        => Latest("compiler", API.LatestMessagesFromCompiler);

    public static IReadOnlyList<object> LatestFromAllRuntimes()
        => Latest("runtimes", API.LatestMessagesFromAllRuntimes);

    public static string MessageSeverity(object message)
        => (ReadMember(message, "Severity") ?? ReadMember(message, "Type"))?.ToString() ?? "";

    public static string? MessageWhat(object message)
        => (ReadMember(message, "What") ?? ReadMember(message, "Text") ?? ReadMember(message, "Message"))?.ToString();

    public static string? MessageWhy(object message)
        => ReadMember(message, "Why")?.ToString();

    private static IReadOnlyList<object> Latest(string key, object? source)
    {
        if (source is null) return [];

        var value = ReadMember(source, "Value");
        if (value is not null) return ToObjects(value);

        lock (Sync)
        {
            if (!Caches.TryGetValue(key, out var cache) || !ReferenceEquals(cache.Source, source))
            {
                cache?.Dispose();
                cache = Subscribe(source);
                Caches[key] = cache;
            }

            return cache.Observer.Latest;
        }
    }

    private static Cache Subscribe(object source)
    {
        var observableType = source.GetType()
            .GetInterfaces()
            .Append(source.GetType())
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IObservable<>));

        if (observableType is null)
            return new Cache(source, EmptyObserver.Instance, null);

        var elementType = observableType.GetGenericArguments()[0];
        var observer = (ILatestObserver)Activator.CreateInstance(typeof(LatestObserver<>).MakeGenericType(elementType))!;
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
    }

    private sealed class LatestObserver<T> : IObserver<T>, ILatestObserver
    {
        private object[] _latest = [];

        public IReadOnlyList<object> Latest => _latest;

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value) => _latest = ToObjects(value);
    }

    private sealed class EmptyObserver : ILatestObserver
    {
        public static readonly EmptyObserver Instance = new();
        public IReadOnlyList<object> Latest => [];
    }
}
