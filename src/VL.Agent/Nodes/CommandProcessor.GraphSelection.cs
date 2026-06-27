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

                if (!TryResolveGraphElement(uid, out var element, out var error))
                    return (false, $"select failed for {target}: {error}");

                items.Add(ModelExtensions.GetCurrent(element));
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
                if (!TryResolveGraphElement(plan.Uid, out var element, out var error))
                    return (false, $"setBounds failed for {plan.Uid}: {error}");

                element = ModelExtensions.GetCurrent(element);
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

    private static bool TryResolveGraphElement(UniqueId uid, out Element element, out string error)
    {
        element = default!;
        error = "";

        if (TryGetSelectedLiveElement(uid, out var liveElement, out _))
        {
            element = liveElement.Element;
            return true;
        }

        if (!TryGetModelElement(uid, out element, out error))
            return false;

        element = ModelExtensions.GetCurrent(element);
        if (element is not (Node or Pad))
        {
            error = $"target is {element.GetType().FullName}, not a node or pad";
            return false;
        }

        return true;
    }
}
