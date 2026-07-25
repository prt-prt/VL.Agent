using System.Text.Json;
using System.Drawing;
using System.Collections;
using System.Reflection;
using VL.Core;
using VL.HDE;
using VL.Lang.Platforms;
using VL.Lang.Symbols;
using VL.Lang.PublicAPI;
using VL.Model;

namespace VL.Agent;

public partial class CommandProcessor
{
    private const int NodeQueryResponseCacheLimit = 128;
    private const int NodeQueryTermCacheLimit = 256;
    private NodeQueryCatalog? _nodeQueryCatalog;

    private string NodeQuery(JsonElement root)
    {
        var query = GetString(root, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Err("nodeQuery requires non-empty query");

        var limit = 12;
        if (root.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number)
            limit = Math.Clamp(limitEl.GetInt32(), 1, 50);

        try
        {
            if (!TryGetActiveCanvas(out var canvas, out var canvasError))
                return Err("nodeQuery failed: " + canvasError);

            var patch = canvas.ParentPatch;
            if (patch is null)
                return Err("nodeQuery failed: active canvas has no parent patch");

            var compilation = VL.Lang.DevEnvHost.Instance?.LatestCompilation;
            if (compilation is null)
                return Err("nodeQuery failed: no live compilation available (DevEnvHost.Instance.LatestCompilation was null)");

            var catalog = GetNodeQueryCatalog(patch, compilation, out var contextReset);
            var responseKey = NodeQueryResponseKey(query, limit);
            if (catalog.Responses.TryGetValue(responseKey, out var cachedResults))
                return NodeQueryResponse(query, cachedResults, contextReset, termHits: 0, termMisses: 0, responseHit: true);

            var resolver = SymbolExtensions.GetResolver(patch, compilation);
            if (resolver is null)
                return Err("nodeQuery failed: could not obtain a symbol resolver for the active patch");

            INodeDefinitionSymbol[] ResolveSymbols(string searchTerm)
            {
                var symbols = new List<INodeDefinitionSymbol>();
                foreach (var result in resolver.Scope.GetSymbols<INodeDefinitionSymbol>(
                             searchTerm,
                             matchSubString: true,
                             includeExtended: true,
                             matcher: _ => true))
                {
                    if (result.Success && result.ResolvedSymbol is { } symbol)
                        symbols.Add(symbol);
                }
                return symbols.ToArray();
            }

            var terms = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var searchTerms = new[] { query.Trim() }.Concat(terms).Distinct(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<INodeDefinitionSymbol>();
            var termHits = 0;
            var termMisses = 0;
            foreach (var term in searchTerms)
                candidates.AddRange(ResolveNodeQueryTerm(catalog, term, ref termHits, ref termMisses, ResolveSymbols));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = candidates
                .Where(c => NodeCandidateMatches(c, terms))
                .Select(c => new { Symbol = c, Key = NodeCandidateKey(c), Score = NodeCandidateScore(c, terms) })
                .Where(c => seen.Add(c.Key))
                .OrderByDescending(c => c.Score)
                .ThenBy(c => SymbolText(c.Symbol, "MetadataCategory") ?? SymbolText(c.Symbol, "Category") ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => NodeDisplayName(c.Symbol), StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(c => NodeCandidateObject(c.Symbol))
                .ToArray();

            CacheNodeQueryResponse(catalog, responseKey, results);
            return NodeQueryResponse(query, results, contextReset, termHits, termMisses, responseHit: false);
        }
        catch (Exception ex)
        {
            return Err("nodeQuery failed: " + ex.Message);
        }
    }

    private NodeQueryCatalog GetNodeQueryCatalog(Patch patch, object compilation, out bool contextReset)
    {
        var patchId = patch.UniqueId.ToString();
        if (_nodeQueryCatalog is { } current
            && current.PatchId == patchId
            && ReferenceEquals(current.Compilation, compilation))
        {
            contextReset = false;
            return current;
        }

        contextReset = true;
        return _nodeQueryCatalog = new NodeQueryCatalog(patchId, compilation);
    }

    private static string NodeQueryResponseKey(string query, int limit)
        => $"{limit}|{NormalizeNodeQueryTerm(query)}";

    private static string NormalizeNodeQueryTerm(string term)
        => term.Trim().ToLowerInvariant();

    private static INodeDefinitionSymbol[] ResolveNodeQueryTerm(
        NodeQueryCatalog catalog,
        string term,
        ref int hits,
        ref int misses,
        Func<string, INodeDefinitionSymbol[]> resolve)
    {
        var key = NormalizeNodeQueryTerm(term);
        if (catalog.SymbolsByTerm.TryGetValue(key, out var cached))
        {
            hits++;
            return cached;
        }

        misses++;
        var resultSymbols = resolve(term);
        catalog.SymbolsByTerm[key] = resultSymbols;
        catalog.TermOrder.Enqueue(key);
        while (catalog.TermOrder.Count > NodeQueryTermCacheLimit)
            catalog.SymbolsByTerm.Remove(catalog.TermOrder.Dequeue());
        return resultSymbols;
    }

    private static void CacheNodeQueryResponse(NodeQueryCatalog catalog, string key, object[] results)
    {
        if (catalog.Responses.ContainsKey(key))
        {
            catalog.Responses[key] = results;
            return;
        }

        catalog.Responses[key] = results;
        catalog.ResponseOrder.Enqueue(key);
        while (catalog.ResponseOrder.Count > NodeQueryResponseCacheLimit)
            catalog.Responses.Remove(catalog.ResponseOrder.Dequeue());
    }

    private static string NodeQueryResponse(
        string query,
        object[] results,
        bool contextReset,
        int termHits,
        int termMisses,
        bool responseHit)
        => JsonSerializer.Serialize(new
        {
            ok = true,
            query,
            count = results.Length,
            candidates = results,
            cache = new { contextReset, termHits, termMisses, responseHit },
        });

    private sealed class NodeQueryCatalog(string patchId, object compilation)
    {
        public string PatchId { get; } = patchId;
        public object Compilation { get; } = compilation;
        public Dictionary<string, INodeDefinitionSymbol[]> SymbolsByTerm { get; } = new(StringComparer.Ordinal);
        public Queue<string> TermOrder { get; } = [];
        public Dictionary<string, object[]> Responses { get; } = new(StringComparer.Ordinal);
        public Queue<string> ResponseOrder { get; } = [];
    }

    private static bool NodeCandidateMatches(INodeDefinitionSymbol symbol, string[] terms)
    {
        if (terms.Length == 0) return true;
        var haystack = NodeCandidateHaystack(symbol);
        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int NodeCandidateScore(INodeDefinitionSymbol symbol, string[] terms)
    {
        var score = symbol.FavorInNodeBrowser ? 100 : 0;
        var name = NodeDisplayName(symbol);
        var category = SymbolText(symbol, "MetadataCategory") ?? SymbolText(symbol, "Category") ?? "";
        foreach (var term in terms)
        {
            if (string.Equals(name, term, StringComparison.OrdinalIgnoreCase)) score += 1000;
            if (string.Equals(category, term, StringComparison.OrdinalIgnoreCase)) score += 200;
            if (StartsWithWord(name, term)) score += 150;
            if (StartsWithWord(category, term)) score += 75;
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 20;
            if (category.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (symbol.Inputs.Concat(symbol.Outputs).Any(p => PinDisplayName(p).Contains(term, StringComparison.OrdinalIgnoreCase))) score += 5;
        }
        return score;
    }

    private static string NodeCandidateHaystack(INodeDefinitionSymbol symbol)
    {
        var pins = symbol.Inputs.Concat(symbol.Outputs)
            .Select(p => $"{PinDisplayName(p)} {SymbolText(p, "Type")}")
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" ", new[]
        {
            NodeDisplayName(symbol),
            SymbolText(symbol, "MetadataCategory") ?? "",
            SymbolText(symbol, "Category") ?? "",
            SymbolText(symbol, "SymbolSource") ?? "",
            symbol.ToString() ?? "",
            string.Join(" ", pins),
        });
    }

    private static string NodeCandidateKey(INodeDefinitionSymbol symbol)
        => $"{NodeKindText(symbol)}|{SymbolText(symbol, "MetadataCategory") ?? SymbolText(symbol, "Category")}|{NodeDisplayName(symbol)}|{symbol}";

    private static object NodeCandidateObject(INodeDefinitionSymbol symbol)
    {
        var kind = NodeKindText(symbol);
        var category = SymbolText(symbol, "MetadataCategory") ?? SymbolText(symbol, "Category") ?? "";
        var name = NodeDisplayName(symbol);
        return new
        {
            symbol = string.IsNullOrWhiteSpace(category) ? $"{kind}::{name}" : $"{kind}:{category}:{name}",
            kind,
            category,
            name,
            display = symbol.ToString(),
            favorInNodeBrowser = symbol.FavorInNodeBrowser,
            pinsReady = symbol.PinsAreReady,
            inputs = symbol.Inputs.Select(PinCandidateObject).ToArray(),
            outputs = symbol.Outputs.Select(PinCandidateObject).ToArray(),
        };
    }

    private static object PinCandidateObject(IPinDefinitionSymbol pin) => new
    {
        name = PinDisplayName(pin),
        type = SymbolText(pin, "Type"),
        isInput = pin.IsInput,
        isOutput = pin.IsOutput,
        isState = pin.IsState,
        visibility = SymbolText(pin, "Visibility"),
    };

    private static string NodeKindText(INodeDefinitionSymbol symbol)
    {
        if (symbol is IProcessDefinitionSymbol) return "process";
        if (symbol is IOperationDefinitionSymbol) return "operation";
        return symbol.ApplicationKinds.Any(k => k.HasFlag(ElementKind.ProcessAppFlag))
            ? "process"
            : "operation";
    }

    private static string NodeDisplayName(INodeDefinitionSymbol symbol)
    {
        var value = ReadMember(symbol, "Name");
        var text = NameLikeText(value);
        if (!string.IsNullOrWhiteSpace(text)) return text;

        text = SymbolText(symbol, "MetadataName");
        if (!string.IsNullOrWhiteSpace(text)) return text;

        return symbol.ToString() ?? "";
    }

    private static string PinDisplayName(IPinDefinitionSymbol pin)
    {
        var text = NameLikeText(ReadMember(pin, "Name"));
        if (!string.IsNullOrWhiteSpace(text)) return text;

        text = SymbolText(pin, "FragmentId");
        if (!string.IsNullOrWhiteSpace(text)) return text;

        return pin.ToString() ?? "";
    }

    private static string? SymbolText(object source, string name)
        => NameLikeText(ReadMember(source, name));

    private static string? NameLikeText(object? value)
    {
        if (value is null) return null;
        var nested = ReadMember(value, "Name");
        if (nested is not null && !ReferenceEquals(nested, value))
            return nested.ToString();
        return value.ToString();
    }

    private static bool StartsWithWord(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;

        foreach (var word in text.Split([' ', '.', ':', '/', '\\', '-', '_', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (word.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
