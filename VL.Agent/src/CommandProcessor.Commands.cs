using System.Text.Json;
using VL.Core;
using VL.HDE;
using VL.Lang.PublicAPI;
using VL.Model;

namespace VL.Agent;

public partial class CommandProcessor
{
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
}
