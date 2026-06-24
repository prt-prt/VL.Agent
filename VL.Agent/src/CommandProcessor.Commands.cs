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
        var plan = new GraphTxPlan();

        PlanTransactionOps(opsEl, plan);
        AddDefaultValidation(tx, opsEl, plan);
        ValidateTransaction(plan, dryRun, solution);

        var shouldApply = !dryRun && plan.Diagnostics.Count == 0 && plan.Unsupported.Count == 0 && plan.PlannedOpCount > 0;
        if (shouldApply)
            shouldApply = ApplyTransaction(plan, context, solution);

        return BuildTransactionResult(plan, dryRun, label!, shouldApply);
    }

    /// <summary>Mutable accumulator for one graph transaction: parsed plans, diagnostics, and apply results.</summary>
    private sealed class GraphTxPlan
    {
        public readonly List<(UniqueId Uid, string Pin, object Value)> SetPins = [];
        public readonly List<SetPinPlan> AliasSetPins = [];
        public readonly List<AddNodePlan> AddNodes = [];
        public readonly List<AddPadPlan> AddPads = [];
        public readonly List<ConnectPlan> Connects = [];
        public readonly List<SetBoundsPlan> SetBounds = [];
        public readonly List<SelectPlan> Selects = [];

        public int Validated;
        public readonly List<string> Unsupported = [];
        public readonly List<string> Diagnostics = [];
        public readonly List<string> Unverified = [];
        public readonly Dictionary<string, string> Created = [];
        public readonly List<string> Connected = [];
        public readonly List<string> Selected = [];

        /// <summary>Add-node symbols resolved up front so dry-run and apply use the same resolution.</summary>
        public List<ResolvedAddNodePlan>? ResolvedAddNodes;

        public int PlannedOpCount =>
            SetPins.Count + AliasSetPins.Count + AddNodes.Count + AddPads.Count
            + Connects.Count + SetBounds.Count + Selects.Count;

        /// <summary>True when the transaction carries a structural edit other than a live-UniqueId setPin.</summary>
        public bool HasStructuralEditsBesidesSetPin =>
            AddNodes.Count > 0 || AddPads.Count > 0 || AliasSetPins.Count > 0
            || Connects.Count > 0 || SetBounds.Count > 0;
    }

    /// <summary>Parses each op into the typed plan lists, recording per-op parse diagnostics.</summary>
    private static void PlanTransactionOps(JsonElement opsEl, GraphTxPlan plan)
    {
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
                        plan.Diagnostics.Add($"setPin target must be '<UniqueId|alias>:<PinName>', got '{target}'");
                        break;
                    }
                    if (!opEl.TryGetProperty("value", out var valueEl))
                    {
                        plan.Diagnostics.Add($"setPin {target}: missing value");
                        break;
                    }

                    var value = Coerce(valueEl, GetString(opEl, "type"));
                    if (value is null)
                    {
                        plan.Diagnostics.Add($"setPin {target}: could not coerce value");
                        break;
                    }
                    if (!UniqueId.TryParse(targetId, out var uid))
                    {
                        plan.AliasSetPins.Add(new SetPinPlan(targetId, pin, value));
                        break;
                    }
                    if (TryGetSelectedTarget(uid, out var selectedTarget) && selectedTarget.Kind != "node")
                    {
                        if (selectedTarget.Kind == "data hub/pad" && string.Equals(pin, "Value", StringComparison.OrdinalIgnoreCase))
                        {
                            plan.SetPins.Add((uid, pin, value));
                            break;
                        }
                        plan.Diagnostics.Add($"setPin currently supports node input pins only; target {target} is selected {selectedTarget.Kind}");
                        break;
                    }
                    if (selectedTarget.Kind == "node" && selectedTarget.Pins.Length > 0 && !selectedTarget.Pins.Contains(pin, StringComparer.OrdinalIgnoreCase))
                    {
                        plan.Diagnostics.Add($"selected node target {target}: no pin named '{pin}'. Known pins: {string.Join(", ", selectedTarget.Pins)}");
                        break;
                    }

                    plan.SetPins.Add((uid, pin, value));
                    break;
                }
                case "addNode":
                {
                    if (TryPlanAddNode(opEl, plan.Diagnostics, out var addNodePlan))
                        plan.AddNodes.Add(addNodePlan);
                    break;
                }
                case "addPad":
                {
                    if (TryPlanAddPad(opEl, plan.Diagnostics, out var addPadPlan))
                        plan.AddPads.Add(addPadPlan);
                    break;
                }
                case "connect":
                {
                    if (TryPlanConnect(opEl, plan.Diagnostics, out var connectPlan))
                        plan.Connects.Add(connectPlan);
                    break;
                }
                case "setBounds":
                {
                    if (TryPlanSetBounds(opEl, plan.Diagnostics, out var setBoundsPlan))
                        plan.SetBounds.Add(setBoundsPlan);
                    break;
                }
                case "select":
                {
                    if (TryPlanSelect(opEl, plan.Diagnostics, out var selectPlan))
                        plan.Selects.Add(selectPlan);
                    break;
                }
                case "validate":
                    plan.Validated += AddValidationDiagnostics(opEl, plan.Diagnostics);
                    break;
                case "disconnect":
                case "annotate":
                    plan.Unsupported.Add(op ?? "<missing>");
                    break;
                default:
                    plan.Diagnostics.Add($"unknown graph op '{op}'");
                    break;
            }
        }
    }

    /// <summary>Runs the default validation pass unless the transaction opted out or already included a validate op.</summary>
    private static void AddDefaultValidation(JsonElement tx, JsonElement opsEl, GraphTxPlan plan)
    {
        var shouldValidate = !tx.TryGetProperty("validate", out var validateEl)
            || validateEl.ValueKind == JsonValueKind.True;
        if (shouldValidate && !opsEl.EnumerateArray().Any(o => GetString(o, "op") == "validate"))
            plan.Validated += AddDefaultValidationDiagnostics(plan.Diagnostics);
    }

    /// <summary>Resolves add-node symbols and records every reason the planned transaction cannot be applied as-is.</summary>
    private static void ValidateTransaction(GraphTxPlan plan, bool dryRun, VL.Lang.PublicAPI.ISolution? solution)
    {
        if (!dryRun && plan.SetPins.Count > 0 && solution is null)
            plan.Diagnostics.Add("setPin failed: no current solution");

        if (plan.AddNodes.Count > 0)
        {
            var resolveResult = TryResolveAddNodes(plan.AddNodes, out var resolved);
            if (!resolveResult.Ok)
                plan.Diagnostics.Add(resolveResult.Detail);
            else
                plan.ResolvedAddNodes = resolved;
        }

        if (!dryRun && plan.AddPads.Count > 0 && !TryGetActiveCanvas(out _, out var activeCanvasError))
            plan.Diagnostics.Add("addPad failed: " + activeCanvasError);

        var creatableAliases = plan.AddPads
            .Select(p => (Alias: p.Alias, Kind: "pad"))
            .Concat(plan.AddNodes.Select(p => (Alias: p.Alias, Kind: "node")))
            .ToArray();
        foreach (var connect in plan.Connects)
        {
            if (!CanResolveConnectEndpoint(connect.From, creatableAliases, out var fromError))
                plan.Diagnostics.Add($"connect from '{connect.From}': {fromError}");
            if (!CanResolveConnectEndpoint(connect.To, creatableAliases, out var toError))
                plan.Diagnostics.Add($"connect to '{connect.To}': {toError}");
        }
        AddPlannedConnectDiagnostics(plan.Connects, plan.ResolvedAddNodes ?? [], plan.AddPads, plan.Diagnostics);

        foreach (var aliasPin in plan.AliasSetPins)
        {
            var padPlan = plan.AddPads.FirstOrDefault(p => string.Equals(p.Alias, aliasPin.Target, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(padPlan.Alias))
            {
                if (!string.Equals(aliasPin.Pin, "Value", StringComparison.OrdinalIgnoreCase))
                    plan.Diagnostics.Add($"setPin {aliasPin.Target}:{aliasPin.Pin}: created pad aliases only support the Value pin");
                continue;
            }

            var nodePlan = plan.AddNodes.FirstOrDefault(p => string.Equals(p.Alias, aliasPin.Target, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(nodePlan.Alias))
            {
                plan.Diagnostics.Add($"setPin target '{aliasPin.Target}:{aliasPin.Pin}' must reference a created alias or live UniqueId");
                continue;
            }

            var nodeDef = plan.ResolvedAddNodes?.FirstOrDefault(p => string.Equals(p.Plan.Alias, aliasPin.Target, StringComparison.Ordinal)).NodeDef;
            if (nodeDef is not null)
            {
                var inputPins = nodeDef.Inputs.Select(PinDisplayName).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                if (inputPins.Length > 0 && !inputPins.Contains(aliasPin.Pin, StringComparer.OrdinalIgnoreCase))
                    plan.Diagnostics.Add($"setPin {aliasPin.Target}:{aliasPin.Pin}: no input pin named '{aliasPin.Pin}'. Known input pins: {string.Join(", ", inputPins)}");
            }
        }

        if (plan.SetBounds.Count > 0 && !plan.SetBounds.All(p => TryGetSelectedLiveElement(p.Uid, out _, out _)))
            plan.Diagnostics.Add("setBounds currently requires each target to be selected in the live editor");
        if (plan.SetPins.Count > 0 && plan.HasStructuralEditsBesidesSetPin)
            plan.Diagnostics.Add("mixing live UniqueId setPin with structural ops in one transaction is not supported yet; use alias setPin for transaction-created nodes or submit separate transactions");
        if ((plan.AddNodes.Count > 0 || plan.AddPads.Count > 0) && plan.SetBounds.Count > 0)
            plan.Diagnostics.Add("mixing creation ops and setBounds in one transaction is not supported yet; set bounds in a follow-up transaction");
        foreach (var selectPlan in plan.Selects)
        {
            foreach (var target in selectPlan.Targets)
            {
                if (plan.AddPads.Any(p => string.Equals(p.Alias, target, StringComparison.Ordinal))
                    || plan.AddNodes.Any(p => string.Equals(p.Alias, target, StringComparison.Ordinal)))
                    continue;
                if (UniqueId.TryParse(target, out var uid) && TryGetSelectedLiveElement(uid, out _, out _))
                    continue;

                plan.Diagnostics.Add(UniqueId.TryParse(target, out _)
                    ? $"select target '{target}' must already be selected in the live editor for this first implementation"
                    : $"select target '{target}' must be a created alias in this transaction or a UniqueId");
            }
        }
    }

    /// <summary>
    /// Applies the planned ops in dependency order (value setPins, then create, then wire, then bounds, then
    /// select) and returns whether every applied stage succeeded. Value setPins need the solution; the structural
    /// stages do not, so a structural-only transaction still applies when the solution is unavailable.
    /// </summary>
    private static bool ApplyTransaction(GraphTxPlan plan, NodeContext context, VL.Lang.PublicAPI.ISolution? solution)
    {
        var shouldApply = true;
        if (solution is not null)
            shouldApply = ApplySetPins(plan, context, solution);

        var createdElements = new Dictionary<string, Element>();
        if (shouldApply && plan.AddNodes.Count > 0)
        {
            var addNodeResult = TryApplyAddNodes(plan.AddNodes, plan.Created, createdElements);
            if (!addNodeResult.Ok)
            {
                plan.Diagnostics.Add(addNodeResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plan.AddPads.Count > 0)
        {
            var addPadResult = TryApplyAddPads(plan.AddPads, plan.Created, out var createdPads);
            if (!addPadResult.Ok)
            {
                plan.Diagnostics.Add(addPadResult.Detail);
                shouldApply = false;
            }
            else
            {
                foreach (var item in createdPads)
                    createdElements[item.Key] = item.Value;
            }
        }

        if (shouldApply && plan.AliasSetPins.Count > 0)
        {
            var setAliasPinsResult = TryApplyAliasSetPins(plan.AliasSetPins, createdElements, plan.Unverified);
            if (!setAliasPinsResult.Ok)
            {
                plan.Diagnostics.Add(setAliasPinsResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plan.Connects.Count > 0)
        {
            var connectResult = TryApplyConnects(plan.Connects, createdElements, plan.Connected);
            if (!connectResult.Ok)
            {
                plan.Diagnostics.Add(connectResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plan.SetBounds.Count > 0)
        {
            var setBoundsResult = TryApplySetBounds(plan.SetBounds);
            if (!setBoundsResult.Ok)
            {
                plan.Diagnostics.Add(setBoundsResult.Detail);
                shouldApply = false;
            }
        }

        if (shouldApply && plan.Selects.Count > 0)
        {
            var selectResult = TryApplySelect(plan.Selects, createdElements, plan.Selected);
            if (!selectResult.Ok)
            {
                plan.Diagnostics.Add(selectResult.Detail);
                shouldApply = false;
            }
        }

        return shouldApply;
    }

    /// <summary>
    /// Applies accumulated value setPins: node pins are batched into one solution <c>Confirm</c>, selected
    /// data-hub pads are written live. Returns false if any setPin produced no observable change or failed.
    /// </summary>
    private static bool ApplySetPins(GraphTxPlan plan, NodeContext context, VL.Lang.PublicAPI.ISolution solution)
    {
        var next = solution;
        var nodeSetPins = new List<(UniqueId Uid, string Pin, object Value)>();
        var padSetPins = new List<(UniqueId Uid, string Pin, object Value)>();
        foreach (var item in plan.SetPins)
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
                plan.Diagnostics.Add($"setPin produced no observable model change for {item.Uid}:{item.Pin}; solution={solution.GetType().FullName}; before={item.Signature}; after={after}");
            else if (!IsInspectableSignature(item.Signature) || !IsInspectableSignature(after))
                plan.Unverified.Add($"{item.Uid}:{item.Pin}");
        }

        var shouldApply = plan.Diagnostics.Count == 0;
        if (shouldApply && nodeSetPins.Count > 0)
            next.Confirm(SetPinUpdateKind);

        if (shouldApply)
        {
            foreach (var (uid, pin, value) in padSetPins)
            {
                var liveResult = TrySetSelectedDataHubValue(uid, value, context);
                if (liveResult.Ok)
                    plan.Unverified.Add($"{uid}:{pin} ({liveResult.Detail})");
                else
                    plan.Diagnostics.Add($"setPin failed for selected data hub/pad {uid}:{pin}: {liveResult.Detail}");
            }
            shouldApply = plan.Diagnostics.Count == 0;
        }

        return shouldApply;
    }

    /// <summary>Serializes the transaction outcome in the stable result shape consumed by vl-mcp.</summary>
    private static string BuildTransactionResult(GraphTxPlan plan, bool dryRun, string label, bool shouldApply)
    {
        var result = new
        {
            ok = plan.Diagnostics.Count == 0 && plan.Unsupported.Count == 0,
            dryRun,
            label,
            partial = !dryRun && plan.Diagnostics.Count > 0 && (plan.Created.Count > 0 || plan.Connected.Count > 0 || plan.Selected.Count > 0),
            appliedOps = shouldApply ? plan.PlannedOpCount : 0,
            checkedOps = plan.PlannedOpCount,
            validationChecks = plan.Validated,
            unsupported = plan.Unsupported,
            unverified = plan.Unverified,
            created = plan.Created,
            connected = plan.Connected,
            selected = plan.Selected,
            diagnostics = plan.Diagnostics,
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
