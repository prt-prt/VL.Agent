using System.Text.Json;
using System.Drawing;
using System.Collections;
using System.Reflection;
using VL.Core;
using VL.Core.Import;
using VL.HDE;
using VL.Lang.Platforms;
using VL.Lang.Symbols;
using VL.Lang.PublicAPI;
using VL.Model;

namespace VL.Agent;

/// <summary>
/// Applies edit requests dropped by an external coding agent. Watches
/// <c>&lt;project&gt;/.agent/requests/*.json</c>, applies each via the undo-integrated
/// <see cref="ISolution"/> API on the main loop, and writes a matching result to
/// <c>&lt;project&gt;/.agent/results/</c>.
/// <para>
/// v1 supports the <c>setPinValue</c> op:
/// <c>{ "op":"setPinValue", "uniqueId":"…", "pin":"…", "value":42, "type":"Int32" }</c>.
/// </para>
/// Runs on the editor's main thread (vvvv calls Update each frame), which is required
/// for solution mutations.
/// </summary>
[ProcessNode(Name = "CommandProcessor")]
public class CommandProcessor
{
    private readonly NodeContext _context;
    private int _applied;

    public CommandProcessor(NodeContext context) => _context = context;

    /// <param name="status">Summary: idle / waiting / "applied N (total M)".</param>
    /// <param name="lastResult">The most recent result line (ok / error).</param>
    /// <param name="path">Override the .agent directory. Empty = &lt;project&gt;/.agent.</param>
    /// <param name="enabled">Set false to stop processing requests.</param>
    public void Update(out string status, out string lastResult, string path = "", bool enabled = true)
    {
        lastResult = _lastResult;
        if (!enabled) { status = "paused"; return; }

        var agentDir = string.IsNullOrWhiteSpace(path) ? ResolveAgentDir() : path;
        if (string.IsNullOrWhiteSpace(agentDir)) { status = "could not resolve project dir — set path"; return; }

        var requestsDir = Path.Combine(agentDir, "requests");
        if (!Directory.Exists(requestsDir)) { status = $"waiting (applied {_applied})"; return; }

        var resultsDir = Path.Combine(agentDir, "results");
        Directory.CreateDirectory(resultsDir);

        string[] files;
        try { files = Directory.GetFiles(requestsDir, "*.json"); }
        catch { files = []; }
        Array.Sort(files, StringComparer.Ordinal);

        int appliedThisFrame = 0;
        foreach (var file in files)
        {
            var result = ProcessFile(file, resultsDir);
            if (result.ImmediateResult is not null)
            {
                _lastResult = result.ImmediateResult;
                lastResult = result.ImmediateResult;
                WriteResult(resultsDir, Path.GetFileName(file), result.ImmediateResult);
            }
            TryDelete(file);
            _applied++;
            appliedThisFrame++;
        }

        status = appliedThisFrame > 0 ? $"applied {appliedThisFrame} (total {_applied})" : $"waiting (applied {_applied})";
    }

    private string _lastResult = "";

    private ProcessResult ProcessFile(string file, string resultsDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var op = GetString(root, "op") ?? "setPinValue";

            var result = op switch
            {
                "setPinValue" => SetPinValue(root, _context),
                "openDocument" => OpenDocument(root),
                "nodeQuery" => NodeQuery(root),
                "graphTransaction" => GraphTransaction(root, _context),
                "paste" => Paste(root, resultsDir, Path.GetFileName(file)),
                _ => Err($"unknown op '{op}'"),
            };
            return result is null ? ProcessResult.Deferred : ProcessResult.Now(result);
        }
        catch (Exception ex)
        {
            return ProcessResult.Now(Err($"{Path.GetFileName(file)}: {ex.Message}"));
        }
    }

    private static string SetPinValue(JsonElement root, NodeContext context)
    {
        var uidStr = GetString(root, "uniqueId");
        var pin = GetString(root, "pin");
        if (string.IsNullOrWhiteSpace(uidStr)) return Err("missing 'uniqueId'");
        if (string.IsNullOrWhiteSpace(pin)) return Err("missing 'pin'");
        if (!UniqueId.TryParse(uidStr, out var uid)) return Err($"unparseable uniqueId '{uidStr}'");
        if (!root.TryGetProperty("value", out var valueEl)) return Err("missing 'value'");

        var typeHint = GetString(root, "type");
        var value = Coerce(valueEl, typeHint);
        if (value is null) return Err($"could not coerce value for type '{typeHint}'");
        if (TryGetSelectedTarget(uid, out var target) && target.Kind != "node")
        {
            if (target.Kind == "data hub/pad" && string.Equals(pin, "Value", StringComparison.OrdinalIgnoreCase))
                return SetSelectedDataHubValue(uid, value, context);
            return Err($"setPinValue currently supports node input pins only; target is selected {target.Kind}");
        }
        if (target.Kind == "node" && target.Pins.Length > 0 && !target.Pins.Contains(pin!, StringComparer.OrdinalIgnoreCase))
            return Err($"selected node has no pin named '{pin}'. Known pins: {string.Join(", ", target.Pins)}");

        var solution = SessionNodes.CurrentSolution;
        if (solution is null) return Err("no current solution");

        var before = PinSignature(solution, uid, pin!);
        var next = solution.SetPinValue(uid, pin!, value);
        var after = PinSignature(next, uid, pin!);
        var verified = IsInspectableSignature(before) && IsInspectableSignature(after);
        if (verified && before == after)
            return Err($"setPin produced no observable model change for {uidStr}:{pin}; solution={solution.GetType().FullName}; before={before}; after={after}");

        next.Confirm(SetPinUpdateKind);
        return Ok($"set {pin}={value} on {uidStr}" + (verified ? "" : $" (unverified; solution={solution.GetType().FullName}; before={before}; after={after})"));
    }

    private static string GraphTransaction(JsonElement root, NodeContext context)
    {
        var tx = root.TryGetProperty("transaction", out var txEl) ? txEl : root;
        if (!tx.TryGetProperty("schemaVersion", out var versionEl) || versionEl.GetInt32() != 1)
            return Err("graphTransaction requires schemaVersion=1");
        var label = GetString(tx, "label");
        if (string.IsNullOrWhiteSpace(label)) return Err("graphTransaction requires non-empty label");
        if (!tx.TryGetProperty("ops", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array)
            return Err("graphTransaction requires ops array");

        var dryRun = GetBool(tx, "dryRun");
        var solution = SessionNodes.CurrentSolution;
        var plannedSetPins = new List<(UniqueId Uid, string Pin, object Value)>();
        var plannedAliasSetPins = new List<SetPinPlan>();
        var plannedAddNodes = new List<AddNodePlan>();
        var plannedAddPads = new List<AddPadPlan>();
        var plannedConnects = new List<ConnectPlan>();
        var plannedSetBounds = new List<SetBoundsPlan>();
        var plannedSelects = new List<SelectPlan>();
        var validated = 0;
        var unsupported = new List<string>();
        var diagnostics = new List<string>();
        var unverified = new List<string>();
        var created = new Dictionary<string, string>();
        var connected = new List<string>();
        var selected = new List<string>();

        foreach (var opEl in opsEl.EnumerateArray())
        {
            var op = GetString(opEl, "op");
            switch (op)
            {
                case "setPin":
                {
                    var target = GetString(opEl, "target");
                    if (!TryParsePinTarget(target, out var targetId, out var pin))
                    {
                        diagnostics.Add($"setPin target must be '<UniqueId|alias>:<PinName>', got '{target}'");
                        break;
                    }
                    if (!opEl.TryGetProperty("value", out var valueEl))
                    {
                        diagnostics.Add($"setPin {target}: missing value");
                        break;
                    }

                    var value = Coerce(valueEl, GetString(opEl, "type"));
                    if (value is null)
                    {
                        diagnostics.Add($"setPin {target}: could not coerce value");
                        break;
                    }
                    if (!UniqueId.TryParse(targetId, out var uid))
                    {
                        plannedAliasSetPins.Add(new SetPinPlan(targetId, pin, value));
                        break;
                    }
                    if (TryGetSelectedTarget(uid, out var selectedTarget) && selectedTarget.Kind != "node")
                    {
                        if (selectedTarget.Kind == "data hub/pad" && string.Equals(pin, "Value", StringComparison.OrdinalIgnoreCase))
                        {
                            plannedSetPins.Add((uid, pin, value));
                            break;
                        }
                        diagnostics.Add($"setPin currently supports node input pins only; target {target} is selected {selectedTarget.Kind}");
                        break;
                    }
                    if (selectedTarget.Kind == "node" && selectedTarget.Pins.Length > 0 && !selectedTarget.Pins.Contains(pin, StringComparer.OrdinalIgnoreCase))
                    {
                        diagnostics.Add($"selected node target {target}: no pin named '{pin}'. Known pins: {string.Join(", ", selectedTarget.Pins)}");
                        break;
                    }

                    plannedSetPins.Add((uid, pin, value));
                    break;
                }
                case "addNode":
                {
                    if (TryPlanAddNode(opEl, diagnostics, out var plan))
                        plannedAddNodes.Add(plan);
                    break;
                }
                case "addPad":
                {
                    if (TryPlanAddPad(opEl, diagnostics, out var plan))
                        plannedAddPads.Add(plan);
                    break;
                }
                case "connect":
                {
                    if (TryPlanConnect(opEl, diagnostics, out var plan))
                        plannedConnects.Add(plan);
                    break;
                }
                case "setBounds":
                {
                    if (TryPlanSetBounds(opEl, diagnostics, out var plan))
                        plannedSetBounds.Add(plan);
                    break;
                }
                case "select":
                {
                    if (TryPlanSelect(opEl, diagnostics, out var plan))
                        plannedSelects.Add(plan);
                    break;
                }
                case "validate":
                    validated += AddValidationDiagnostics(opEl, diagnostics);
                    break;
                case "disconnect":
                case "annotate":
                    unsupported.Add(op ?? "<missing>");
                    break;
                default:
                    diagnostics.Add($"unknown graph op '{op}'");
                    break;
            }
        }

        var shouldValidate = !tx.TryGetProperty("validate", out var validateEl)
            || validateEl.ValueKind == JsonValueKind.True;
        if (shouldValidate && !opsEl.EnumerateArray().Any(o => GetString(o, "op") == "validate"))
            validated += AddDefaultValidationDiagnostics(diagnostics);

        if (!dryRun && plannedSetPins.Count > 0 && solution is null)
            diagnostics.Add("setPin failed: no current solution");
        List<ResolvedAddNodePlan>? resolvedAddNodes = null;
        if (plannedAddNodes.Count > 0)
        {
            var resolveResult = TryResolveAddNodes(plannedAddNodes, out var resolved);
            if (!resolveResult.Ok)
                diagnostics.Add(resolveResult.Detail);
            else
                resolvedAddNodes = resolved;
        }
        if (!dryRun && plannedAddPads.Count > 0 && !TryGetActiveCanvas(out _, out var activeCanvasError))
            diagnostics.Add("addPad failed: " + activeCanvasError);
        var creatableAliases = plannedAddPads
            .Select(p => (Alias: p.Alias, Kind: "pad"))
            .Concat(plannedAddNodes.Select(p => (Alias: p.Alias, Kind: "node")))
            .ToArray();
        foreach (var plan in plannedConnects)
        {
            if (!CanResolveConnectEndpoint(plan.From, creatableAliases, out var fromError))
                diagnostics.Add($"connect from '{plan.From}': {fromError}");
            if (!CanResolveConnectEndpoint(plan.To, creatableAliases, out var toError))
                diagnostics.Add($"connect to '{plan.To}': {toError}");
        }
        AddPlannedConnectDiagnostics(plannedConnects, resolvedAddNodes ?? [], plannedAddPads, diagnostics);
        foreach (var plan in plannedAliasSetPins)
        {
            var padPlan = plannedAddPads.FirstOrDefault(p => string.Equals(p.Alias, plan.Target, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(padPlan.Alias))
            {
                if (!string.Equals(plan.Pin, "Value", StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add($"setPin {plan.Target}:{plan.Pin}: created pad aliases only support the Value pin");
                continue;
            }

            var nodePlan = plannedAddNodes.FirstOrDefault(p => string.Equals(p.Alias, plan.Target, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(nodePlan.Alias))
            {
                diagnostics.Add($"setPin target '{plan.Target}:{plan.Pin}' must reference a created alias or live UniqueId");
                continue;
            }

            var nodeDef = resolvedAddNodes?.FirstOrDefault(p => string.Equals(p.Plan.Alias, plan.Target, StringComparison.Ordinal)).NodeDef;
            if (nodeDef is not null)
            {
                var inputPins = nodeDef.Inputs.Select(PinDisplayName).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                if (inputPins.Length > 0 && !inputPins.Contains(plan.Pin, StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add($"setPin {plan.Target}:{plan.Pin}: no input pin named '{plan.Pin}'. Known input pins: {string.Join(", ", inputPins)}");
            }
        }
        if (plannedSetBounds.Count > 0 && !plannedSetBounds.All(p => TryGetSelectedLiveElement(p.Uid, out _, out _)))
            diagnostics.Add("setBounds currently requires each target to be selected in the live editor");
        if (plannedSetPins.Count > 0 && (plannedAddNodes.Count > 0 || plannedAddPads.Count > 0 || plannedAliasSetPins.Count > 0 || plannedConnects.Count > 0 || plannedSetBounds.Count > 0))
            diagnostics.Add("mixing live UniqueId setPin with structural ops in one transaction is not supported yet; use alias setPin for transaction-created nodes or submit separate transactions");
        if ((plannedAddNodes.Count > 0 || plannedAddPads.Count > 0) && plannedSetBounds.Count > 0)
            diagnostics.Add("mixing creation ops and setBounds in one transaction is not supported yet; set bounds in a follow-up transaction");
        foreach (var plan in plannedSelects)
        {
            foreach (var target in plan.Targets)
            {
                if (plannedAddPads.Any(p => string.Equals(p.Alias, target, StringComparison.Ordinal))
                    || plannedAddNodes.Any(p => string.Equals(p.Alias, target, StringComparison.Ordinal)))
                    continue;
                if (UniqueId.TryParse(target, out var uid) && TryGetSelectedLiveElement(uid, out _, out _))
                    continue;

                diagnostics.Add(UniqueId.TryParse(target, out _)
                    ? $"select target '{target}' must already be selected in the live editor for this first implementation"
                    : $"select target '{target}' must be a created alias in this transaction or a UniqueId");
            }
        }

        var shouldApply = !dryRun && diagnostics.Count == 0 && unsupported.Count == 0 && (plannedSetPins.Count > 0 || plannedAliasSetPins.Count > 0 || plannedAddNodes.Count > 0 || plannedAddPads.Count > 0 || plannedConnects.Count > 0 || plannedSetBounds.Count > 0 || plannedSelects.Count > 0);
        if (shouldApply && solution is not null)
        {
            var next = solution;
            var nodeSetPins = new List<(UniqueId Uid, string Pin, object Value)>();
            var padSetPins = new List<(UniqueId Uid, string Pin, object Value)>();
            foreach (var item in plannedSetPins)
            {
                if (string.Equals(item.Pin, "Value", StringComparison.OrdinalIgnoreCase)
                    && TryGetSelectedTarget(item.Uid, out var selectedTarget)
                    && selectedTarget.Kind == "data hub/pad")
                    padSetPins.Add(item);
                else
                    nodeSetPins.Add(item);
            }

            var before = nodeSetPins
                .Select(p => (p.Uid, p.Pin, Signature: PinSignature(solution, p.Uid, p.Pin)))
                .ToArray();
            foreach (var (uid, pin, value) in nodeSetPins)
                next = next.SetPinValue(uid, pin, value);

            foreach (var item in before)
            {
                var after = PinSignature(next, item.Uid, item.Pin);
                if (IsInspectableSignature(item.Signature) && IsInspectableSignature(after) && item.Signature == after)
                    diagnostics.Add($"setPin produced no observable model change for {item.Uid}:{item.Pin}; solution={solution.GetType().FullName}; before={item.Signature}; after={after}");
                else if (!IsInspectableSignature(item.Signature) || !IsInspectableSignature(after))
                    unverified.Add($"{item.Uid}:{item.Pin}");
            }

            shouldApply = diagnostics.Count == 0;
            if (shouldApply && nodeSetPins.Count > 0)
                next.Confirm(SetPinUpdateKind);

            if (shouldApply)
            {
                foreach (var (uid, pin, value) in padSetPins)
                {
                    var liveResult = TrySetSelectedDataHubValue(uid, value, context);
                    if (liveResult.Ok)
                        unverified.Add($"{uid}:{pin} ({liveResult.Detail})");
                    else
                        diagnostics.Add($"setPin failed for selected data hub/pad {uid}:{pin}: {liveResult.Detail}");
                }
                shouldApply = diagnostics.Count == 0;
            }
        }

        var createdElements = new Dictionary<string, Element>();
        if (shouldApply && plannedAddNodes.Count > 0)
        {
            var addNodeResult = TryApplyAddNodes(plannedAddNodes, created, createdElements);
            if (!addNodeResult.Ok)
            {
                diagnostics.Add(addNodeResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plannedAddPads.Count > 0)
        {
            var addPadResult = TryApplyAddPads(plannedAddPads, created, out var createdPads);
            if (!addPadResult.Ok)
            {
                diagnostics.Add(addPadResult.Detail);
                shouldApply = false;
            }
            else
            {
                foreach (var item in createdPads)
                    createdElements[item.Key] = item.Value;
            }
        }

        if (shouldApply && plannedAliasSetPins.Count > 0)
        {
            var setAliasPinsResult = TryApplyAliasSetPins(plannedAliasSetPins, createdElements, unverified);
            if (!setAliasPinsResult.Ok)
            {
                diagnostics.Add(setAliasPinsResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plannedConnects.Count > 0)
        {
            var connectResult = TryApplyConnects(plannedConnects, createdElements, connected);
            if (!connectResult.Ok)
            {
                diagnostics.Add(connectResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plannedSetBounds.Count > 0)
        {
            var setBoundsResult = TryApplySetBounds(plannedSetBounds);
            if (!setBoundsResult.Ok)
            {
                diagnostics.Add(setBoundsResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plannedSelects.Count > 0)
        {
            var selectResult = TryApplySelect(plannedSelects, createdElements, selected);
            if (!selectResult.Ok)
            {
                diagnostics.Add(selectResult.Detail);
                shouldApply = false;
            }
        }

        var result = new
        {
            ok = diagnostics.Count == 0 && unsupported.Count == 0,
            dryRun,
            label,
            partial = !dryRun && diagnostics.Count > 0 && (created.Count > 0 || connected.Count > 0 || selected.Count > 0),
            appliedOps = shouldApply ? plannedSetPins.Count + plannedAliasSetPins.Count + plannedAddNodes.Count + plannedAddPads.Count + plannedConnects.Count + plannedSetBounds.Count + plannedSelects.Count : 0,
            checkedOps = plannedSetPins.Count + plannedAliasSetPins.Count + plannedAddNodes.Count + plannedAddPads.Count + plannedConnects.Count + plannedSetBounds.Count + plannedSelects.Count,
            validationChecks = validated,
            unsupported,
            unverified,
            created,
            connected,
            selected,
            diagnostics,
        };
        return JsonSerializer.Serialize(result);
    }

    private static string OpenDocument(JsonElement root)
    {
        var p = GetString(root, "path");
        if (string.IsNullOrWhiteSpace(p)) return Err("missing 'path'");
        if (!File.Exists(p)) return Err($"file not found: {p}");

        SessionNodes.OpenDocument(VL.Lib.IO.Path.FilePath(p));
        return Ok($"opened {p}");
    }

    private static string NodeQuery(JsonElement root)
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

            var resolver = SymbolExtensions.GetResolver(patch, compilation);
            if (resolver is null)
                return Err("nodeQuery failed: could not obtain a symbol resolver for the active patch");

            var terms = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var searchTerms = new[] { query.Trim() }.Concat(terms).Distinct(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<INodeDefinitionSymbol>();
            foreach (var term in searchTerms)
            {
                foreach (var result in resolver.Scope.GetSymbols<INodeDefinitionSymbol>(
                             term,
                             matchSubString: true,
                             includeExtended: true,
                             matcher: _ => true))
                {
                    if (result.Success && result.ResolvedSymbol is { } symbol)
                        candidates.Add(symbol);
                }
            }

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

            var response = new
            {
                ok = true,
                query,
                count = results.Length,
                candidates = results,
            };
            return JsonSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            return Err("nodeQuery failed: " + ex.Message);
        }
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

    private readonly record struct AddNodePlan(
        string Alias,
        string Name,
        string Category,
        string Dependency,
        ElementKind ReferenceKind,
        Rectangle2 Bounds);

    private readonly record struct SetPinPlan(
        string Target,
        string Pin,
        object Value);

    private static bool TryPlanAddNode(JsonElement opEl, List<string> diagnostics, out AddNodePlan plan)
    {
        plan = default;

        var alias = GetString(opEl, "alias");
        var symbol = GetString(opEl, "symbol");
        var dependency = GetString(opEl, "dependency") ?? "VL.CoreLib.vl";
        if (string.IsNullOrWhiteSpace(alias))
        {
            diagnostics.Add("addNode requires non-empty alias");
            return false;
        }
        if (string.IsNullOrWhiteSpace(symbol))
        {
            diagnostics.Add($"addNode {alias}: requires non-empty symbol");
            return false;
        }

        var parts = symbol.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            diagnostics.Add($"addNode {alias}: symbol must be '<operation|process>:<category>:<name>', got '{symbol}'");
            return false;
        }

        var referenceKind = parts[0].ToLowerInvariant() switch
        {
            "operation" or "op" => ElementKind.OperationCallFlag,
            "process" or "proc" => ElementKind.ProcessAppFlag,
            _ => ElementKind.None,
        };
        if (referenceKind == ElementKind.None)
        {
            diagnostics.Add($"addNode {alias}: symbol kind must be operation or process, got '{parts[0]}'");
            return false;
        }

        var bounds = new Rectangle2(new Point2(0, 0), Size2.Empty);
        if (opEl.TryGetProperty("bounds", out var boundsEl) && boundsEl.ValueKind == JsonValueKind.Array)
        {
            var values = boundsEl.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.Number)
                .Select(v => v.GetDouble())
                .ToArray();
            if (values.Length >= 2)
            {
                var position = new Point2((int)Math.Round(values[0]), (int)Math.Round(values[1]));
                var size = values.Length >= 4
                    ? new Size2((int)Math.Round(values[2]), (int)Math.Round(values[3]))
                    : Size2.Empty;
                bounds = new Rectangle2(position, size);
            }
            else
            {
                diagnostics.Add($"addNode {alias}: bounds must contain at least x and y");
                return false;
            }
        }

        plan = new AddNodePlan(alias, parts[2], parts[1], dependency, referenceKind, bounds);
        return true;
    }

    private static (bool Ok, string Detail) TryApplyAddNodes(
        List<AddNodePlan> plans,
        Dictionary<string, string> created,
        Dictionary<string, Element> createdElements)
    {
        var stage = "start";
        try
        {
            var resolveResult = TryResolveAddNodes(plans, out var resolved);
            if (!resolveResult.Ok)
                return resolveResult;

            var canvas = resolved[0].Canvas;
            var currentCanvas = canvas;
            foreach (var item in resolved)
            {
                var plan = item.Plan;
                stage = "resolve active patch";
                var patch = currentCanvas.ParentPatch;
                if (patch is null)
                    return (false, "active canvas has no parent patch");

                stage = "add node";
                var position = new PointF(plan.Bounds.Position.X, plan.Bounds.Position.Y);
                var node = ModelExtensions.AddNode(
                    patch,
                    position,
                    item.NodeRef,
                    item.NodeDef,
                    currentCanvas,
                    default,
                    item.NodeDef.MetadataCategory,
                    IdentityNameProvider.Instance,
                    assignToLayer: false);

                stage = "commit solution";
                var nextSolution = ModelExtensions.ReplaceDescendent(node.Solution, node);
                ModelExtensions.MakeCurrent(nextSolution, SetPinUpdateKind, currentCanvas);

                var currentNode = ModelExtensions.GetCurrent(node);
                created[plan.Alias] = currentNode.UniqueId.ToString();
                createdElements[plan.Alias] = currentNode;
                currentCanvas = ModelExtensions.GetCurrent(currentCanvas);
            }

            return (true, $"created {plans.Count} node(s)");
        }
        catch (Exception ex)
        {
            return (false, $"addNode failed during {stage}: {ex.Message}");
        }
    }

    private readonly record struct ResolvedAddNodePlan(
        AddNodePlan Plan,
        Canvas Canvas,
        ChoiceBasedNodeReference NodeRef,
        INodeDefinitionSymbol NodeDef);

    private static (bool Ok, string Detail) TryResolveAddNodes(
        List<AddNodePlan> plans,
        out List<ResolvedAddNodePlan> resolved)
    {
        resolved = [];
        try
        {
            if (!TryGetActiveCanvas(out var canvas, out var error))
                return (false, "addNode failed: " + error);

            var patch = canvas.ParentPatch;
            if (patch is null)
                return (false, "addNode failed: active canvas has no parent patch");

            var compilation = VL.Lang.DevEnvHost.Instance?.LatestCompilation;
            if (compilation is null)
                return (false, "addNode failed: no live compilation available (DevEnvHost.Instance.LatestCompilation was null)");

            var resolver = SymbolExtensions.GetResolver(patch, compilation);
            if (resolver is null)
                return (false, "addNode failed: could not obtain a symbol resolver for the active patch");

            foreach (var plan in plans)
            {
                var queryName = TryGetBaseNodeName(plan.Name, out var baseName)
                    ? baseName
                    : plan.Name;
                INodeDefinitionSymbol? exactScopedNodeDef = null;
                foreach (var result in resolver.Scope.GetSymbols<INodeDefinitionSymbol>(
                             queryName,
                             matchSubString: true,
                             includeExtended: true,
                             matcher: _ => true))
                {
                    if (!result.Success || result.ResolvedSymbol is not INodeDefinitionSymbol symbol)
                        continue;
                    if (!CategoryMatches(SymbolText(symbol, "MetadataCategory"), plan.Category)
                        && !CategoryMatches(SymbolText(symbol, "Category"), plan.Category))
                        continue;
                    if (!string.Equals(NodeDisplayName(symbol), plan.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (plan.ReferenceKind == ElementKind.ProcessAppFlag && symbol is not IProcessDefinitionSymbol)
                        continue;
                    if (plan.ReferenceKind == ElementKind.OperationCallFlag && symbol is not IOperationDefinitionSymbol)
                        continue;

                    exactScopedNodeDef = symbol;
                    break;
                }

                var nodeRef = new ChoiceBasedNodeReference(
                    new NameAndVersion(plan.Name, ""),
                    ElementKind.NodeFlag | plan.ReferenceKind,
                    Array.Empty<IChoice>());
                nodeRef.LastCategoryFullName = plan.Category;
                nodeRef.LastDependency = plan.Dependency;

                var candidates = resolver.GetCandidates(nodeRef, autoChooseLastCategory: true);
                if (candidates.IsDefaultOrEmpty)
                {
                    if (!string.Equals(queryName, plan.Name, StringComparison.Ordinal))
                    {
                        nodeRef = new ChoiceBasedNodeReference(
                            new NameAndVersion(queryName, ""),
                            ElementKind.NodeFlag | plan.ReferenceKind,
                            Array.Empty<IChoice>());
                        nodeRef.LastCategoryFullName = plan.Category;
                        nodeRef.LastDependency = plan.Dependency;
                        candidates = resolver.GetCandidates(nodeRef, autoChooseLastCategory: true);
                    }
                }
                if (candidates.IsDefaultOrEmpty && exactScopedNodeDef is null)
                    return (false, $"addNode {plan.Alias}: symbol '{plan.Category}:{plan.Name}' did not resolve to any node definition (check category/name/dependency)");

                var nodeDef = exactScopedNodeDef ?? candidates[0].Item1;
                if (exactScopedNodeDef is null)
                {
                    foreach (var candidate in candidates)
                    {
                        if (string.Equals(NodeDisplayName(candidate.Item1), plan.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            nodeDef = candidate.Item1;
                            break;
                        }
                    }
                }
                else
                {
                    nodeRef = ReferenceToSymbol.ToNodeReference(exactScopedNodeDef, resolver, plan.ReferenceKind);
                }
                NodeRefHelpers.NormalizeNodeReferenceOnCreation(nodeRef, nodeDef);
                resolved.Add(new ResolvedAddNodePlan(plan, canvas, nodeRef, nodeDef));
            }

            return (true, $"resolved {resolved.Count} node(s)");
        }
        catch (Exception ex)
        {
            return (false, "addNode failed during symbol resolution: " + ex.Message);
        }
    }

    private static bool TryGetBaseNodeName(string name, out string baseName)
    {
        baseName = name;
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren <= 0)
            return false;

        baseName = name[..paren].Trim();
        return !string.IsNullOrWhiteSpace(baseName);
    }

    private static bool CategoryMatches(string? actual, string requested)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(requested))
            return false;
        return string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase)
            || actual.EndsWith("." + requested, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Identity name provider: keeps the node's requested name unchanged.</summary>
    private sealed class IdentityNameProvider : INameProvider
    {
        public static readonly IdentityNameProvider Instance = new();
        public NameAndVersion GetName(NameAndVersion name) => name;
    }

    private static (bool Ok, string Detail) TryApplyAliasSetPins(
        List<SetPinPlan> plans,
        Dictionary<string, Element> createdElements,
        List<string> unverified)
    {
        var stage = "start";
        try
        {
            var solution = SessionNodes.CurrentSolution;
            if (solution is null)
                return (false, "setPin failed: no current solution");

            var next = solution;
            var before = new List<(string Target, UniqueId Uid, string Pin, string Signature)>();
            foreach (var plan in plans)
            {
                stage = $"resolve alias {plan.Target}";
                if (!createdElements.TryGetValue(plan.Target, out var element))
                    return (false, $"setPin target '{plan.Target}:{plan.Pin}' was not created in this transaction");

                stage = $"set {plan.Target}:{plan.Pin}";
                var uid = element.UniqueId;
                before.Add((plan.Target, uid, plan.Pin, PinSignature(next, uid, plan.Pin)));
                next = next.SetPinValue(uid, plan.Pin, plan.Value);
            }

            foreach (var item in before)
            {
                var after = PinSignature(next, item.Uid, item.Pin);
                if (IsInspectableSignature(item.Signature) && IsInspectableSignature(after) && item.Signature == after)
                    return (false, $"setPin produced no observable model change for {item.Target}:{item.Pin}; before={item.Signature}; after={after}");
                if (!IsInspectableSignature(item.Signature) || !IsInspectableSignature(after))
                    unverified.Add($"{item.Target}:{item.Pin}");
            }

            stage = "commit";
            next.Confirm(SetPinUpdateKind);

            foreach (var item in createdElements.ToArray())
                createdElements[item.Key] = ModelExtensions.GetCurrent(item.Value);

            return (true, $"set {plans.Count} alias pin(s)");
        }
        catch (Exception ex)
        {
            return (false, $"setPin failed during {stage}: {ex.Message}");
        }
    }

    private readonly record struct AddPadPlan(
        string Alias,
        string Name,
        string Type,
        TypeReference TypeReference,
        Type ClrType,
        object Value,
        Point2 Position,
        bool ShowValueBox);

    private static bool TryPlanAddPad(JsonElement opEl, List<string> diagnostics, out AddPadPlan plan)
    {
        plan = default;

        var alias = GetString(opEl, "alias");
        var name = GetString(opEl, "name");
        var type = GetString(opEl, "type");
        if (string.IsNullOrWhiteSpace(alias))
        {
            diagnostics.Add("addPad requires non-empty alias");
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add($"addPad {alias}: requires non-empty name");
            return false;
        }
        if (string.IsNullOrWhiteSpace(type) || !TryMapPadType(type, out var typeReference, out var clrType))
        {
            diagnostics.Add($"addPad {alias}: unsupported type '{type}'. Supported: Boolean, Int32, Float32, Float64, String");
            return false;
        }

        object value = DefaultValueFor(clrType);
        if (opEl.TryGetProperty("value", out var valueEl))
        {
            value = Coerce(valueEl, type);
            value = ConvertForClrType(value, clrType);
        }

        var position = new Point2(0, 0);
        if (opEl.TryGetProperty("bounds", out var boundsEl) && boundsEl.ValueKind == JsonValueKind.Array)
        {
            var values = boundsEl.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToArray();
            if (values.Length >= 2)
                position = new Point2((int)Math.Round(values[0]), (int)Math.Round(values[1]));
            else
                diagnostics.Add($"addPad {alias}: bounds must contain at least x and y");
        }

        var showValueBox = !opEl.TryGetProperty("showValueBox", out var showEl) || showEl.ValueKind != JsonValueKind.False;
        plan = new AddPadPlan(alias, name, type, typeReference, clrType, value, position, showValueBox);
        return true;
    }

    private static (bool Ok, string Detail) TryApplyAddPads(
        List<AddPadPlan> plans,
        Dictionary<string, string> created,
        out Dictionary<string, Element> createdElements)
    {
        createdElements = new Dictionary<string, Element>();
        try
        {
            if (!TryGetActiveCanvas(out var canvas, out var error))
                return (false, error);

            var currentCanvas = canvas;
            foreach (var plan in plans)
            {
                var patch = currentCanvas.ParentPatch;
                if (patch is null)
                    return (false, "active canvas has no parent patch");

                var converted = ConvertForClrType(plan.Value, plan.ClrType);
                var compileTimeValue = CompileTimeValue.From(converted, wrapNull: true, currentCanvas.UniqueId, plan.ClrType);
                var pad = patch.AddPad(plan.Position, currentCanvas, plan.TypeReference, compileTimeValue, plan.ShowValueBox)
                    .WithComment(plan.Name);

                var nextSolution = ModelExtensions.ReplaceDescendent(pad.Solution, pad);
                ModelExtensions.MakeCurrent(nextSolution, SetPinUpdateKind, currentCanvas);

                created[plan.Alias] = pad.UniqueId.ToString();
                createdElements[plan.Alias] = pad;
                currentCanvas = ModelExtensions.GetCurrent(currentCanvas);
            }

            return (true, $"created {plans.Count} pad(s)");
        }
        catch (Exception ex)
        {
            return (false, "addPad failed: " + ex.Message);
        }
    }

    private readonly record struct ConnectPlan(string From, string To);

    private static bool TryPlanConnect(JsonElement opEl, List<string> diagnostics, out ConnectPlan plan)
    {
        plan = default;
        var from = GetString(opEl, "from");
        var to = GetString(opEl, "to");
        if (string.IsNullOrWhiteSpace(from))
        {
            diagnostics.Add("connect requires non-empty from endpoint");
            return false;
        }
        if (string.IsNullOrWhiteSpace(to))
        {
            diagnostics.Add("connect requires non-empty to endpoint");
            return false;
        }

        plan = new ConnectPlan(from, to);
        return true;
    }

    private readonly record struct PlannedEndpointInfo(
        string Endpoint,
        string Target,
        string? Pin,
        string? Type,
        bool IsInput,
        bool IsOutput,
        bool IsKnown);

    private static void AddPlannedConnectDiagnostics(
        List<ConnectPlan> plans,
        List<ResolvedAddNodePlan> resolvedAddNodes,
        List<AddPadPlan> plannedAddPads,
        List<string> diagnostics)
    {
        foreach (var plan in plans)
        {
            var from = ResolvePlannedEndpoint(plan.From, resolvedAddNodes, plannedAddPads);
            var to = ResolvePlannedEndpoint(plan.To, resolvedAddNodes, plannedAddPads);

            if (from.IsKnown && !from.IsOutput)
                diagnostics.Add($"connect from '{plan.From}': pin '{from.Pin}' is not an output pin");
            if (to.IsKnown && !to.IsInput)
                diagnostics.Add($"connect to '{plan.To}': pin '{to.Pin}' is not an input pin");
            if (from.IsKnown && to.IsKnown && !PinTypesCompatible(from.Type, to.Type))
                diagnostics.Add($"connect {plan.From} -> {plan.To}: type mismatch {NormalizePinType(from.Type)} -> {NormalizePinType(to.Type)}");
        }
    }

    private static PlannedEndpointInfo ResolvePlannedEndpoint(
        string endpoint,
        List<ResolvedAddNodePlan> resolvedAddNodes,
        List<AddPadPlan> plannedAddPads)
    {
        var (target, pin) = SplitEndpoint(endpoint);

        var padPlan = plannedAddPads.FirstOrDefault(p => string.Equals(p.Alias, target, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(padPlan.Alias))
            return new PlannedEndpointInfo(endpoint, target, pin, padPlan.Type, IsInput: true, IsOutput: true, IsKnown: true);

        var nodePlan = resolvedAddNodes.FirstOrDefault(p => string.Equals(p.Plan.Alias, target, StringComparison.Ordinal));
        if (nodePlan.NodeDef is null)
            return new PlannedEndpointInfo(endpoint, target, pin, null, IsInput: false, IsOutput: false, IsKnown: false);

        if (string.IsNullOrWhiteSpace(pin))
            return new PlannedEndpointInfo(endpoint, target, pin, null, IsInput: false, IsOutput: false, IsKnown: true);

        var pinDef = nodePlan.NodeDef.Inputs
            .Concat(nodePlan.NodeDef.Outputs)
            .FirstOrDefault(p => string.Equals(PinDisplayName(p), pin, StringComparison.OrdinalIgnoreCase));
        if (pinDef is null)
            return new PlannedEndpointInfo(endpoint, target, pin, null, IsInput: false, IsOutput: false, IsKnown: false);

        return new PlannedEndpointInfo(
            endpoint,
            target,
            pin,
            SymbolText(pinDef, "Type"),
            pinDef.IsInput,
            pinDef.IsOutput,
            IsKnown: true);
    }

    private static bool PinTypesCompatible(string? sourceType, string? sinkType)
    {
        var source = NormalizePinType(sourceType);
        var sink = NormalizePinType(sinkType);
        return string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(sink)
            || string.Equals(source, sink, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePinType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return "";

        var text = type.Trim();
        return text.ToLowerInvariant() switch
        {
            "single" or "float" => "Float32",
            "double" => "Float64",
            "int" or "int32" or "integer" => "Integer32",
            "bool" => "Boolean",
            _ => text,
        };
    }

    private static bool CanResolveConnectEndpoint(string endpoint, IEnumerable<(string Alias, string Kind)> creatableAliases, out string error)
    {
        error = "";
        var (target, pin) = SplitEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "endpoint must be an alias, a UniqueId, or <UniqueId>:<PinName>";
            return false;
        }

        var createdAlias = creatableAliases.FirstOrDefault(p => string.Equals(p.Alias, target, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(createdAlias.Alias))
        {
            if (createdAlias.Kind == "pad")
                return true;

            if (!string.IsNullOrWhiteSpace(pin))
                return true;

            error = "created node endpoint requires :<PinName>";
            return false;
        }

        if (!UniqueId.TryParse(target, out var uid))
        {
            error = "target is not a transaction-created alias or UniqueId";
            return false;
        }

        if (TryGetModelDataHubEndpoint(uid, pin, out _, out error))
            return true;

        if (TryGetSelectedDataHubEndpoint(uid, pin, out _, out error))
            return true;

        return false;
    }

    private static (bool Ok, string Detail) TryApplyConnects(
        List<ConnectPlan> plans,
        Dictionary<string, Element> createdElements,
        List<string> connected)
    {
        var stage = "start";
        try
        {
            foreach (var plan in plans)
            {
                stage = $"resolve source {plan.From}";
                if (!TryResolveConnectEndpoint(plan.From, createdElements, out var source, out var fromError))
                    return (false, $"connect from '{plan.From}': {fromError}");
                stage = $"resolve sink {plan.To}";
                if (!TryResolveConnectEndpoint(plan.To, createdElements, out var sink, out var toError))
                    return (false, $"connect to '{plan.To}': {toError}");

                stage = "refresh endpoints";
                source = ModelExtensions.GetCurrent(source);
                sink = ModelExtensions.GetCurrent(sink);

                stage = "validate direction";
                if (!source.CanBeSource(sink))
                    return (false, $"connect {plan.From} -> {plan.To}: source cannot connect to sink");
                if (!sink.CanBeSink(source))
                    return (false, $"connect {plan.From} -> {plan.To}: sink cannot accept source");

                stage = "resolve patch";
                var patch = source.SourcePatch ?? sink.SinkPatch ?? source.SinkPatch ?? sink.SourcePatch;
                if (patch is null)
                    return (false, $"connect {plan.From} -> {plan.To}: could not resolve containing patch");

                stage = "create link";
                var link = patch.GetOrAddLink([source, sink]);
                if (link is null)
                    return (false, $"connect {plan.From} -> {plan.To}: GetOrAddLink returned null");
                var updatedPatch = link.ContainingPatch;
                if (updatedPatch is null)
                    return (false, $"connect {plan.From} -> {plan.To}: link has no containing patch");

                stage = "resolve undo canvas";
                var canvas = source.Proxy?.ParentCanvas
                    ?? sink.Proxy?.ParentCanvas
                    ?? patch.DefaultCanvas;
                if (canvas is null && !TryGetActiveCanvas(out canvas, out _))
                    return (false, $"connect {plan.From} -> {plan.To}: could not resolve undo canvas");

                stage = "commit link patch";
                var nextSolution = ModelExtensions.ReplaceDescendent(patch.Solution, updatedPatch);
                ModelExtensions.MakeCurrent(nextSolution, SetPinUpdateKind, canvas);

                stage = "verify committed link";
                source = ModelExtensions.GetCurrent(source);
                sink = ModelExtensions.GetCurrent(sink);
                if (!HasModelLink(source, sink))
                    return (false, $"connect {source.Identity}->{sink.Identity}: link was not found after commit");

                connected.Add($"{source.Identity}->{sink.Identity}");
            }

            return (true, $"connected {plans.Count} link(s)");
        }
        catch (Exception ex)
        {
            return (false, $"connect failed during {stage}: {ex.Message}");
        }
    }

    private static bool TryResolveConnectEndpoint(
        string endpoint,
        Dictionary<string, Element> createdElements,
        out DataHub hub,
        out string error)
    {
        hub = default!;
        error = "";
        var (target, pin) = SplitEndpoint(endpoint);

        if (createdElements.TryGetValue(target, out var created))
        {
            if (created is DataHub createdHub)
            {
                if (!string.IsNullOrWhiteSpace(pin) && !string.Equals(pin, "Value", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"created pad endpoint only supports no pin or :Value, got :{pin}";
                    return false;
                }

                hub = createdHub;
                return true;
            }

            if (TryGetModelNodePin(created, pin, out hub, out error))
                return true;

            error = $"created alias '{target}': {error}";
            return false;
        }

        if (!UniqueId.TryParse(target, out var uid))
        {
            error = "target is not a transaction-created alias or UniqueId";
            return false;
        }

        if (TryGetModelDataHubEndpoint(uid, pin, out hub, out error))
            return true;

        return TryGetSelectedDataHubEndpoint(uid, pin, out hub, out error);
    }

    private static bool HasModelLink(DataHub source, DataHub sink)
    {
        try
        {
            var sourceId = source.Identity.ToString();
            var sinkId = sink.Identity.ToString();
            var inspectedAnyLink = false;

            foreach (var link in EnumerateMemberValues(source, "OutgoingLinks")
                         .Concat(EnumerateMemberValues(sink, "IncomingLinks")))
            {
                inspectedAnyLink = true;
                if (LinkMatches(link, sourceId, sinkId))
                    return true;
            }

            // Older public API surfaces may not expose link collections uniformly.
            // In that case MakeCurrent returning successfully is the strongest signal available.
            return !inspectedAnyLink;
        }
        catch
        {
            return true;
        }
    }

    private static bool LinkMatches(object? link, string sourceId, string sinkId)
    {
        if (link is null)
            return false;

        var source = ReadMember(link, "Source")
            ?? ReadMember(link, "SourceHub")
            ?? ReadMember(link, "SourceId")
            ?? ReadMember(link, "SourceIdentity");
        var sink = ReadMember(link, "Sink")
            ?? ReadMember(link, "SinkHub")
            ?? ReadMember(link, "SinkId")
            ?? ReadMember(link, "SinkIdentity")
            ?? ReadMember(link, "Target")
            ?? ReadMember(link, "TargetHub");

        return string.Equals(ModelIdentityText(source), sourceId, StringComparison.Ordinal)
            && string.Equals(ModelIdentityText(sink), sinkId, StringComparison.Ordinal);
    }

    private static string? ModelIdentityText(object? value)
    {
        if (value is null)
            return null;

        return (ReadMember(value, "Identity")
                ?? ReadMember(value, "UniqueId")
                ?? ReadMember(value, "Id")
                ?? value)
            .ToString();
    }

    private static IEnumerable<object?> EnumerateMemberValues(object source, string memberName)
    {
        var value = ReadMember(source, memberName);
        if (value is null || value is string)
            yield break;

        var values = ReadMember(value, "Values");
        if (values is IEnumerable dictionaryValues)
        {
            foreach (var item in dictionaryValues)
                yield return item;
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                yield return item;
        }
    }

    private static bool TryGetModelDataHubEndpoint(UniqueId uid, string? pin, out DataHub hub, out string error)
    {
        hub = default!;
        error = "";

        if (!TryGetModelElement(uid, out var element, out error))
            return false;

        element = ModelExtensions.GetCurrent(element);
        if (element is DataHub dataHub)
        {
            if (!string.IsNullOrWhiteSpace(pin) && !string.Equals(pin, "Value", StringComparison.OrdinalIgnoreCase))
            {
                error = $"pad endpoint only supports no pin or :Value, got :{pin}";
                return false;
            }

            hub = dataHub;
            return true;
        }

        if (TryGetModelNodePin(element, pin, out hub, out error))
            return true;

        error = $"model target '{uid}': {error}";
        return false;
    }

    private static bool TryGetModelElement(UniqueId uid, out Element element, out string error)
    {
        element = default!;
        error = "";
        var uidText = uid.ToString();

        try
        {
            var modelSolution = VL.Lang.DevEnvHost.Instance?.CurrentSolution;
            if (modelSolution is not null)
            {
                var modelElement = modelSolution.GetDescendent(uid);
                if (modelElement is not null)
                {
                    element = modelElement;
                    return true;
                }

                foreach (var document in modelSolution.Documents)
                {
                    if (TryFindElementInTree(document.Patch, uidText, out element))
                        return true;
                }
            }

            var solution = SessionNodes.CurrentSolution;
            if (solution is not null && GetDescendent(solution, uid) is Element solutionElement)
            {
                element = solutionElement;
                return true;
            }

            if (TryGetActiveCanvas(out var canvas, out _))
            {
                var patch = canvas.ParentPatch;
                if (patch is not null)
                {
                    if (TryFindElementInTree(patch, uidText, out element))
                        return true;
                    if (TryFindElementInModelCollection(ReadMember(patch, "ParticipatingElements"), uidText, out element))
                        return true;
                }

                if (TryFindElementInModelCollection(ReadMember(canvas, "Elements"), uidText, out element))
                    return true;
            }

            error = "target was not found in the current solution or active patch model";
            return false;
        }
        catch (Exception ex)
        {
            error = "could not inspect active patch model: " + ex.Message;
            return false;
        }
    }

    private static bool TryFindElementInTree(Element root, string uidText, out Element element)
    {
        element = default!;
        try
        {
            foreach (var candidate in ModelExtensions.GetSelfAndDescendents(root))
            {
                if (candidate is null)
                    continue;

                if (string.Equals(candidate.UniqueId.ToString(), uidText, StringComparison.Ordinal))
                {
                    element = candidate;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool TryFindElementInModelCollection(object? collection, string uidText, out Element element)
    {
        element = default!;
        if (collection is null || collection is string)
            return false;

        var enumerable = ReadMember(collection, "Values") as IEnumerable ?? collection as IEnumerable;
        if (enumerable is null)
            return false;

        foreach (var item in enumerable)
        {
            var candidate = item as Element
                ?? ReadMember(item!, "Value") as Element
                ?? ReadMember(item!, "Element") as Element;
            if (candidate is null)
                continue;

            if (string.Equals(candidate.UniqueId.ToString(), uidText, StringComparison.Ordinal))
            {
                element = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetModelNodePin(Element element, string? pin, out DataHub hub, out string error)
    {
        hub = default!;
        error = "";
        if (string.IsNullOrWhiteSpace(pin))
        {
            error = "created node endpoint requires :<PinName>";
            return false;
        }

        var pins = ReadMember(element, "Pins");
        if (pins is not IEnumerable items)
        {
            error = $"{element.GetType().FullName} has no inspectable Pins collection";
            return false;
        }

        var knownPins = new List<string>();
        foreach (var item in items)
        {
            if (item is null) continue;
            var name = ReadMember(item, "Name")?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                knownPins.Add(name);
            if (!string.Equals(name, pin, StringComparison.OrdinalIgnoreCase)) continue;

            if (item is DataHub dataHub)
            {
                hub = dataHub;
                return true;
            }

            error = $"pin '{pin}' is {item.GetType().FullName}, not a data hub";
            return false;
        }

        error = $"created node has no pin named '{pin}'";
        if (knownPins.Count > 0)
            error += $". Known pins: {string.Join(", ", knownPins)}";
        return false;
    }

    private static (string Target, string? Pin) SplitEndpoint(string endpoint)
    {
        var index = endpoint.LastIndexOf(':');
        if (index < 0)
            return (endpoint.Trim(), null);
        return (endpoint[..index].Trim(), endpoint[(index + 1)..].Trim());
    }

    private readonly record struct SetBoundsPlan(UniqueId Uid, Rectangle2 Bounds);

    private readonly record struct SelectPlan(string[] Targets);

    private static bool TryPlanSelect(JsonElement opEl, List<string> diagnostics, out SelectPlan plan)
    {
        plan = default;
        if (!opEl.TryGetProperty("targets", out var targetsEl) || targetsEl.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add("select requires targets array");
            return false;
        }

        var targets = targetsEl.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToArray();

        if (targets.Length == 0)
        {
            diagnostics.Add("select requires at least one non-empty target");
            return false;
        }

        plan = new SelectPlan(targets);
        return true;
    }

    private static (bool Ok, string Detail) TryApplySelect(
        List<SelectPlan> plans,
        Dictionary<string, Element> createdElements,
        List<string> selected)
    {
        try
        {
            var items = new List<object>();
            foreach (var target in plans.SelectMany(p => p.Targets))
            {
                if (createdElements.TryGetValue(target, out var createdElement))
                {
                    items.Add(ModelExtensions.GetCurrent(createdElement));
                    selected.Add(createdElement.UniqueId.ToString());
                    continue;
                }

                if (!UniqueId.TryParse(target, out var uid))
                    return (false, $"select target '{target}' is neither a created alias nor a UniqueId");

                if (!TryGetSelectedLiveElement(uid, out var liveElement, out var error))
                    return (false, $"select failed for {target}: {error}");

                items.Add(liveElement);
                selected.Add(uid.ToString());
            }

            var channel = API.CurrentSelection;
            if (channel is null)
                return (false, "selection channel is not available");

            channel.Value = VL.Lib.Collections.Spread.Create(items.ToArray());
            return (true, $"selected {items.Count} element(s)");
        }
        catch (Exception ex)
        {
            return (false, "select failed: " + ex.Message);
        }
    }

    private static bool TryPlanSetBounds(JsonElement opEl, List<string> diagnostics, out SetBoundsPlan plan)
    {
        plan = default;

        var target = GetString(opEl, "target");
        if (string.IsNullOrWhiteSpace(target))
        {
            diagnostics.Add("setBounds requires non-empty target");
            return false;
        }
        if (!UniqueId.TryParse(target, out var uid))
        {
            diagnostics.Add($"setBounds target must be a UniqueId for this first implementation, got '{target}'");
            return false;
        }
        if (!opEl.TryGetProperty("bounds", out var boundsEl) || boundsEl.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add($"setBounds {target}: bounds array is required");
            return false;
        }

        var values = boundsEl.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetDouble())
            .ToArray();
        if (values.Length is not (2 or 4))
        {
            diagnostics.Add($"setBounds {target}: bounds must be [x,y] or [x,y,width,height]");
            return false;
        }

        var position = new Point2((int)Math.Round(values[0]), (int)Math.Round(values[1]));
        var size = values.Length == 4
            ? new Size2((int)Math.Round(values[2]), (int)Math.Round(values[3]))
            : Size2.Empty;
        plan = new SetBoundsPlan(uid, new Rectangle2(position, size));
        return true;
    }

    private static (bool Ok, string Detail) TryApplySetBounds(List<SetBoundsPlan> plans)
    {
        try
        {
            var edits = new List<(Element Original, Element Next, Canvas Canvas)>();
            foreach (var plan in plans)
            {
                if (!TryGetSelectedLiveElement(plan.Uid, out var live, out var error))
                    return (false, $"setBounds failed for {plan.Uid}: {error}");

                var element = live.Element;
                var canvas = element.ParentCanvas ?? element.Document?.Canvas;
                if (canvas is null)
                    return (false, $"setBounds failed for {plan.Uid}: target has no parent canvas");

                Element nextElement = element switch
                {
                    Node node => node.WithBounds(plan.Bounds, true),
                    Pad pad => pad.WithBounds(plan.Bounds, true),
                    _ => throw new InvalidOperationException($"target is {element.GetType().FullName}, not a node or pad"),
                };

                edits.Add((element, nextElement, canvas));
            }

            if (edits.Count == 0)
                return (true, "set bounds on 0 element(s)");

            var undoCanvas = edits[0].Canvas;
            var nextSolution = edits[0].Original.Solution;
            foreach (var edit in edits)
                nextSolution = ModelExtensions.ReplaceDescendent(nextSolution, edit.Next);

            ModelExtensions.MakeCurrent(nextSolution, SetPinUpdateKind, undoCanvas);
            return (true, $"set bounds on {edits.Count} element(s)");
        }
        catch (Exception ex)
        {
            return (false, "setBounds failed: " + ex.Message);
        }
    }

    private static bool TryGetActiveCanvas(out Canvas canvas, out string error)
    {
        canvas = default!;
        error = "";

        try
        {
            var liveCanvas = API.ActiveLiveCanvasStream?.Value;
            if (liveCanvas?.Canvas is null)
            {
                error = "no active live canvas; focus a patch canvas in the vvvv editor";
                return false;
            }

            canvas = liveCanvas.Canvas;
            return true;
        }
        catch (Exception ex)
        {
            error = "could not read active live canvas: " + ex.Message;
            return false;
        }
    }

    private static bool TryMapPadType(string type, out TypeReference typeReference, out Type clrType)
    {
        switch (type.Trim().ToLowerInvariant())
        {
            case "bool":
            case "boolean":
                typeReference = TypeReference.BooleanRef;
                clrType = typeof(bool);
                return true;
            case "int":
            case "int32":
            case "integer":
            case "integer32":
                typeReference = TypeReference.Integer32Ref;
                clrType = typeof(int);
                return true;
            case "float":
            case "single":
            case "float32":
                typeReference = TypeReference.Float32Ref;
                clrType = typeof(float);
                return true;
            case "double":
            case "float64":
                typeReference = TypeReference.Float64Ref;
                clrType = typeof(double);
                return true;
            case "string":
                typeReference = TypeReference.StringRef;
                clrType = typeof(string);
                return true;
            default:
                typeReference = default!;
                clrType = default!;
                return false;
        }
    }

    private static object DefaultValueFor(Type clrType)
        => clrType == typeof(string) ? ""
         : clrType == typeof(bool) ? false
         : clrType == typeof(float) ? 0f
         : clrType == typeof(double) ? 0d
         : clrType == typeof(int) ? 0
         : "";

    // Experimental: direct SessionNodes.Paste from this node's Update was observed
    // to race the graphical editor render pass. This path requires an explicit
    // opt-in and posts the mutation to the UI synchronization context so it runs
    // after the current Update call returns. Keep this behind MCP/dev tooling until
    // repeated in-vvvv testing proves it stable.
    private static string? Paste(JsonElement root, string resultsDir, string requestFileName)
    {
        var snippet = GetString(root, "snippet");
        if (string.IsNullOrWhiteSpace(snippet)) return Err("missing 'snippet'");

        if (!GetBool(root, "experimental"))
            return Err("paste requires experimental=true; this is an opt-in dev path because paste mutates the live editor graph.");

        var context = SynchronizationContext.Current;
        if (context is null) return Err("no SynchronizationContext available for deferred paste");

        var location = new PointF(GetFloat(root, "x"), GetFloat(root, "y"));
        var pauseRuntime = GetBool(root, "pauseRuntime");
        var leaveRuntimePaused = GetBool(root, "leaveRuntimePaused");
        _pendingPastes++;
        context.Post(_ =>
        {
            string result;
            var paused = new List<(RuntimeHost Host, RunMode Mode)>();
            try
            {
                if (pauseRuntime)
                {
                    var runtime = VLSession.Instance?.UserRuntime
                        ?? throw new InvalidOperationException("no user runtime available for runtime pause");
                    if (runtime is not null)
                    {
                        var mode = runtime.Mode;
                        paused.Add((runtime, mode));
                        runtime.SwitchMode(RunMode.Paused);
                    }
                }

                SessionNodes.Paste(snippet!, location);
                result = Ok($"pasted snippet at {location.X},{location.Y}"
                    + (pauseRuntime ? leaveRuntimePaused ? " (runtime left paused)" : " (runtime was paused during paste)" : ""));
            }
            catch (Exception ex)
            {
                result = Err("deferred paste failed: " + ex.Message);
            }
            finally
            {
                if (pauseRuntime && !leaveRuntimePaused)
                {
                    foreach (var (runtime, mode) in paused)
                    {
                        try { runtime.SwitchMode(mode); } catch { }
                    }
                }
                _pendingPastes--;
            }
            WriteResult(resultsDir, requestFileName, result);
        }, null);

        return null;
    }

    private static int _pendingPastes;

    private static float GetFloat(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? (float)el.GetDouble() : 0f;

    private static bool TryParsePinTarget(string? target, out string uniqueId, out string pin)
    {
        uniqueId = "";
        pin = "";
        if (string.IsNullOrWhiteSpace(target)) return false;

        var i = target.LastIndexOf(':');
        if (i <= 0 || i == target.Length - 1) return false;

        uniqueId = target[..i].Trim();
        pin = target[(i + 1)..].Trim();
        return uniqueId.Length > 0 && pin.Length > 0;
    }

    private static readonly SolutionUpdateKind SetPinUpdateKind =
        SolutionUpdateKind.CommitToValue | SolutionUpdateKind.UpdateUIAndRuntime;

    private static string PinSignature(VL.Lang.PublicAPI.ISolution solution, UniqueId uid, string pin)
    {
        try
        {
            var element = GetDescendent(solution, uid);
            if (element is null) return "<missing element>";

            var pinValue = FindPinValue(element, pin);
            if (pinValue.Found) return $"pin:{pin}={FormatValue(pinValue.Value)}";

            var memberValue = ReadMember(element, pin);
            if (memberValue is not null) return $"member:{pin}={FormatValue(memberValue)}";

            return $"element:{element.GetType().FullName}:{element}";
        }
        catch (Exception ex)
        {
            return "<signature error: " + ex.GetType().Name + ">";
        }
    }

    private static bool IsInspectableSignature(string signature)
        => !signature.StartsWith("<", StringComparison.Ordinal);

    private static bool TryGetSelectedTarget(UniqueId uid, out SelectedTarget target)
    {
        target = default;
        try
        {
            var selection = API.CurrentSelection?.Value;
            if (selection is null) return false;
            var uidText = uid.ToString();

            foreach (var item in selection)
            {
                if (item is not ILiveElement live) continue;
                var element = live.Element;
                if (element is null || !string.Equals(element.UniqueId.ToString(), uidText, StringComparison.Ordinal)) continue;

                var kind = item is ILiveNodeApplication ? "node"
                    : item is ILiveDataHub ? "data hub/pad"
                    : item.GetType().Name;
                var pins = item is ILiveNodeApplication node
                    ? node.Pins.Select(p => p.Info.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray()
                    : [];
                target = new SelectedTarget(kind, pins);
                return true;
            }
        }
        catch { }

        return false;
    }

    private readonly record struct SelectedTarget(string Kind, string[] Pins);

    private static bool TryGetSelectedLiveElement(UniqueId uid, out ILiveElement liveElement, out string error)
    {
        liveElement = default!;
        error = "";

        try
        {
            var selection = API.CurrentSelection?.Value;
            if (selection is null)
            {
                error = "no current selection";
                return false;
            }

            var uidText = uid.ToString();
            foreach (var item in selection)
            {
                if (item is not ILiveElement live) continue;
                var element = live.Element;
                if (element is null) continue;
                if (!string.Equals(element.UniqueId.ToString(), uidText, StringComparison.Ordinal)) continue;
                if (element is not (Node or Pad))
                {
                    error = $"selected target is {element.GetType().FullName}, not a node or pad";
                    return false;
                }

                liveElement = live;
                return true;
            }

            error = "target is not selected";
            return false;
        }
        catch (Exception ex)
        {
            error = "could not inspect current selection: " + ex.Message;
            return false;
        }
    }

    private static bool TryGetSelectedDataHubEndpoint(UniqueId uid, string? pin, out DataHub hub, out string error)
    {
        hub = default!;
        error = "";

        try
        {
            var selection = API.CurrentSelection?.Value;
            if (selection is null)
            {
                error = "no current selection";
                return false;
            }

            var uidText = uid.ToString();
            foreach (var item in selection)
            {
                if (item is ILiveDataHub selectedHub
                    && string.Equals(selectedHub.Element.UniqueId.ToString(), uidText, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(pin) && !string.Equals(pin, "Value", StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"selected pad endpoint only supports no pin or :Value, got :{pin}";
                        return false;
                    }

                    hub = selectedHub.Element;
                    return true;
                }

                if (item is not ILiveNodeApplication node) continue;
                var element = node.Element;
                if (element is null || !string.Equals(element.UniqueId.ToString(), uidText, StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(pin))
                {
                    error = "selected node endpoint requires :<PinName>";
                    return false;
                }

                foreach (var livePin in node.Pins)
                {
                    if (!string.Equals(livePin.Info.Name, pin, StringComparison.OrdinalIgnoreCase)) continue;
                    hub = livePin.Element;
                    return true;
                }

                error = $"selected node has no pin named '{pin}'. Known pins: {string.Join(", ", node.Pins.Select(p => p.Info.Name).Where(n => !string.IsNullOrWhiteSpace(n)))}";
                return false;
            }

            error = "target is not selected";
            return false;
        }
        catch (Exception ex)
        {
            error = "could not inspect current selection: " + ex.Message;
            return false;
        }
    }

    private static string SetSelectedDataHubValue(UniqueId uid, object value, NodeContext context)
    {
        var result = TrySetSelectedDataHubValue(uid, value, context);
        return result.Ok
            ? Ok($"set selected data hub/pad Value={value} on {uid} ({result.Detail})")
            : Err($"could not set selected data hub/pad value for {uid}: {result.Detail}");
    }

    private static (bool Ok, string Detail) TrySetSelectedDataHubValue(UniqueId uid, object value, NodeContext context)
    {
        var failures = new List<string>();
        try
        {
            if (!TryGetSelectedDataHub(uid, out var hub)) return (false, "selected data hub/pad not found");
            var before = FormatValue(hub.Info.Value);

            var modelResult = TryMakeCurrentPadValue(hub, value, before);
            if (modelResult.Ok)
                return modelResult;
            failures.Add(modelResult.Detail);

            var propertyResult = TrySetAssociatedPropertyDefault(hub, value, context, before);
            if (propertyResult.Ok)
                return propertyResult;
            failures.Add(propertyResult.Detail);

            var channel = hub.CreateDataChannel();
            using var change = channel.BeginChange();
            channel.Object = value;
            var method = channel.GetType().GetMethod("SetObjectAndAuthor", BindingFlags.Instance | BindingFlags.Public);
            if (method is not null)
                method.Invoke(channel, [value, "agentic-vl"]);
            else
                channel.GetType().GetProperty("Object", BindingFlags.Instance | BindingFlags.Public)?.SetValue(channel, value);
            channel.ChannelOfObject.SetValueAndAuthor(value, "agentic-vl");
            var after = FormatValue(hub.Info.Value);
            if (ValuesMatch(after, value))
                return (true, $"live data channel; {before} -> {after}; not model-verified");

            failures.Add($"live data channel did not update visible value; before={before}; after={after}; requested={FormatValue(value)}");
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        return (false, string.Join("; ", failures.Where(f => !string.IsNullOrWhiteSpace(f))));
    }

    private static (bool Ok, string Detail) TryMakeCurrentPadValue(ILiveDataHub hub, object value, string visibleBefore)
    {
        try
        {
            if (hub.Element is not Pad pad)
                return (false, $"live data hub element is {hub.Element?.GetType().FullName ?? "<null>"}, not VL.Model.Pad");

            var currentSolution = pad.Document?.Parent;
            if (currentSolution is null) return (false, "pad has no current solution parent");

            var canvas = pad.ParentCanvas ?? pad.Document?.Canvas;
            if (canvas is null) return (false, "pad has no parent canvas");

            var channel = hub.CreateDataChannel();
            var clrType = channel.ClrTypeOfValues;
            if (clrType == typeof(object) || clrType == typeof(void))
                clrType = value.GetType();
            var converted = ConvertForClrType(value, clrType);
            var compileTimeValue = CompileTimeValue.From(converted, wrapNull: true, pad.UniqueId, clrType);
            var nextPad = pad.WithValue(compileTimeValue);
            var nextSolution = ModelExtensions.ReplaceDescendent(currentSolution, nextPad);

            ModelExtensions.MakeCurrent(nextSolution, SetPinUpdateKind, canvas);

            var visibleAfter = FormatValue(hub.Info.Value);
            return ValuesMatch(visibleAfter, converted)
                ? (true, $"model MakeCurrent pad value; visible {visibleBefore} -> {visibleAfter}; serialized {FormatValue(pad.SerializedValue)} -> {FormatValue(nextPad.SerializedValue)}")
                : (false, $"model MakeCurrent pad value did not update visible value; visible before={visibleBefore}; visible after={visibleAfter}; requested={FormatValue(converted)}; serialized {FormatValue(pad.SerializedValue)} -> {FormatValue(nextPad.SerializedValue)}");
        }
        catch (Exception ex)
        {
            return (false, "model MakeCurrent pad value failed: " + ex.Message);
        }
    }

    private static (bool Ok, string Detail) TrySetAssociatedPropertyDefault(ILiveDataHub hub, object value, NodeContext context, string visibleBefore)
    {
        try
        {
            var property = hub.AssociatedProperty;
            if (property is null) return (false, "associated property not available");
            if (!property.AllowsDefault) return (false, "associated property does not allow default values");

            var channel = hub.CreateDataChannel();
            var clrType = channel.ClrTypeOfValues;
            if (clrType == typeof(object) || clrType == typeof(void))
                clrType = value.GetType();

            var converted = ConvertForClrType(value, clrType);
            var beforeDefault = FormatOptional(property.GetDefault(clrType, context));
            var optional = CreateOptional(converted, clrType);
            property.SetDefault(optional, clrType, context);

            var visibleAfter = FormatValue(hub.Info.Value);
            var defaultAfter = FormatOptional(property.GetDefault(clrType, context));
            if (ValuesMatch(visibleAfter, converted))
                return (true, $"associated property default; visible {visibleBefore} -> {visibleAfter}; default {beforeDefault} -> {defaultAfter}");

            return (false, $"associated property default did not update visible value; visible before={visibleBefore}; visible after={visibleAfter}; default before={beforeDefault}; default after={defaultAfter}; requested={FormatValue(converted)}");
        }
        catch (Exception ex)
        {
            return (false, "associated property default failed: " + ex.Message);
        }
    }

    private static bool TryGetSelectedDataHub(UniqueId uid, out ILiveDataHub hub)
    {
        hub = default!;
        try
        {
            var selection = API.CurrentSelection?.Value;
            if (selection is null) return false;
            var uidText = uid.ToString();

            foreach (var item in selection)
            {
                if (item is not ILiveDataHub candidate) continue;
                if (!string.Equals(candidate.Element.UniqueId.ToString(), uidText, StringComparison.Ordinal)) continue;
                hub = candidate;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static object? GetDescendent(VL.Lang.PublicAPI.ISolution solution, UniqueId uid)
    {
        var method = solution.GetType().GetMethod(
            "GetDescendent",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(UniqueId)],
            modifiers: null);

        return method?.Invoke(solution, [uid]);
    }

    private static (bool Found, object? Value) FindPinValue(object element, string pinName)
    {
        var pins = ReadMember(element, "Pins");
        if (pins is not IEnumerable items) return (false, null);

        foreach (var pin in items)
        {
            var name = ReadMember(pin!, "Name")?.ToString();
            if (!string.Equals(name, pinName, StringComparison.OrdinalIgnoreCase)) continue;
            return (true, ReadMember(pin!, "SerializedValue") ?? ReadMember(pin!, "Value") ?? pin);
        }

        return (false, null);
    }

    private static object? ReadMember(object source, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var type = source.GetType();
        return type.GetProperty(name, flags)?.GetValue(source)
            ?? type.GetField(name, flags)?.GetValue(source);
    }

    private static string FormatValue(object? value)
        => value switch
        {
            null => "<null>",
            string s => s,
            IEnumerable e when value is not string => string.Join(",", e.Cast<object>().Select(o => o?.ToString() ?? "<null>")),
            _ => value.ToString() ?? "",
        };

    private static string FormatOptional(IOptional optional)
    {
        try
        {
            return optional.HasValue ? FormatValue(optional.Object) : "<no value>";
        }
        catch (Exception ex)
        {
            return "<optional read failed: " + ex.Message + ">";
        }
    }

    private static object ConvertForClrType(object value, Type clrType)
    {
        try
        {
            var target = Nullable.GetUnderlyingType(clrType) ?? clrType;
            if (target.IsInstanceOfType(value)) return value;
            if (target.IsEnum && value is string s) return Enum.Parse(target, s, ignoreCase: true);
            if (target == typeof(string)) return FormatValue(value);
            return Convert.ChangeType(value, target);
        }
        catch
        {
            return value;
        }
    }

    private static IOptional CreateOptional(object value, Type clrType)
    {
        var method = typeof(OptionalExtensions).GetMethod(
            "CreateOptional",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance,
            binder: null,
            types: [typeof(object), typeof(Type)],
            modifiers: null);
        if (method is null)
            throw new MissingMethodException(typeof(OptionalExtensions).FullName, "CreateOptional(object, Type)");

        var target = method.IsStatic ? null : Activator.CreateInstance(typeof(OptionalExtensions));
        var optional = method.Invoke(target, [value, clrType]);
        return optional is IOptional typed
            ? typed
            : throw new InvalidOperationException($"CreateOptional returned {optional?.GetType().FullName ?? "<null>"}");
    }

    private static bool ValuesMatch(string visibleValue, object requestedValue)
        => string.Equals(
            visibleValue.Trim(),
            FormatValue(requestedValue).Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static int AddValidationDiagnostics(JsonElement opEl, List<string> diagnostics)
    {
        if (!opEl.TryGetProperty("checks", out var checksEl) || checksEl.ValueKind != JsonValueKind.Array)
            return AddDefaultValidationDiagnostics(diagnostics);

        var count = 0;
        foreach (var checkEl in checksEl.EnumerateArray())
        {
            if (checkEl.ValueKind != JsonValueKind.String) continue;
            count++;
            switch (checkEl.GetString())
            {
                case "compile":
                    AddCompilerDiagnostics(diagnostics);
                    break;
                case "runtimeMessages":
                    AddRuntimeDiagnostics(diagnostics);
                    break;
                case "links":
                case "selection":
                    // Reserved for the richer graph model. No-op for the first transaction slice.
                    break;
                default:
                    diagnostics.Add($"unknown validation check '{checkEl.GetString()}'");
                    break;
            }
        }
        return count;
    }

    private static int AddDefaultValidationDiagnostics(List<string> diagnostics)
    {
        AddCompilerDiagnostics(diagnostics);
        return 1;
    }

    private static void AddCompilerDiagnostics(List<string> diagnostics)
    {
        try
        {
            foreach (var m in EditorMessages.LatestCompiler())
                diagnostics.Add($"compiler:{EditorMessages.MessageSeverity(m)}: {EditorMessages.MessageWhat(m)} {EditorMessages.MessageWhy(m)}".Trim());
        }
        catch (Exception ex)
        {
            diagnostics.Add("compiler validation failed: " + ex.Message);
        }
    }

    private static void AddRuntimeDiagnostics(List<string> diagnostics)
    {
        try
        {
            foreach (var m in EditorMessages.LatestFromAllRuntimes())
                diagnostics.Add($"runtime:{EditorMessages.MessageSeverity(m)}: {EditorMessages.MessageWhat(m)} {EditorMessages.MessageWhy(m)}".Trim());
        }
        catch (Exception ex)
        {
            diagnostics.Add("runtime validation failed: " + ex.Message);
        }
    }

    /// <summary>Coerce a JSON value to the CLR type vvvv expects for the pin.</summary>
    private static object? Coerce(JsonElement v, string? type)
    {
        switch ((type ?? "").ToLowerInvariant())
        {
            case "int32" or "integer32" or "int" or "integer": return v.TryGetInt32(out var i) ? i : (int)v.GetDouble();
            case "int64" or "integer64" or "long": return v.GetInt64();
            case "float32" or "float" or "single": return (float)v.GetDouble();
            case "float64" or "double": return v.GetDouble();
            case "boolean" or "bool": return v.GetBoolean();
            case "string": return v.GetString();
        }

        // No hint: infer from the JSON token.
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : v.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => v.GetBoolean(),
            JsonValueKind.String => v.GetString(),
            _ => v.ToString(),
        };
    }

    private string? ResolveAgentDir()
    {
        try
        {
            var stack = _context.Path.Stack;
            if (stack.IsEmpty) return null;
            var docPath = _context.AppHost.GetDocumentPath(stack.Peek());
            if (string.IsNullOrEmpty(docPath)) return null;
            var dir = Path.GetDirectoryName(docPath);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, AgentConvention.DirName);
        }
        catch { return null; }
    }

    private static void WriteResult(string resultsDir, string requestFileName, string result)
    {
        try
        {
            var path = Path.Combine(resultsDir, requestFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, result);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string Ok(string msg) => "{\"ok\":true,\"message\":" + JsonSerializer.Serialize(msg) + "}";
    private static string Err(string msg) => "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(msg) + "}";

    private readonly record struct ProcessResult(string? ImmediateResult)
    {
        public static ProcessResult Now(string result) => new(result);
        public static ProcessResult Deferred => new(null);
    }
}
