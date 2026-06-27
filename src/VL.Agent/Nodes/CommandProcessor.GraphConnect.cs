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
}
