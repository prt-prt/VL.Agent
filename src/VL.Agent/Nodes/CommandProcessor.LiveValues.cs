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
                method.Invoke(channel, [value, "VL.Agent"]);
            else
                channel.GetType().GetProperty("Object", BindingFlags.Instance | BindingFlags.Public)?.SetValue(channel, value);
            channel.ChannelOfObject.SetValueAndAuthor(value, "VL.Agent");
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
}
