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
}
