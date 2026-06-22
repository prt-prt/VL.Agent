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
        var validated = 0;
        var unsupported = new List<string>();
        var diagnostics = new List<string>();
        var unverified = new List<string>();

        foreach (var opEl in opsEl.EnumerateArray())
        {
            var op = GetString(opEl, "op");
            switch (op)
            {
                case "setPin":
                {
                    var target = GetString(opEl, "target");
                    if (!TryParsePinTarget(target, out var uidStr, out var pin))
                    {
                        diagnostics.Add($"setPin target must be '<UniqueId>:<PinName>', got '{target}'");
                        break;
                    }
                    if (!UniqueId.TryParse(uidStr, out var uid))
                    {
                        diagnostics.Add($"setPin target has unparseable UniqueId '{uidStr}'");
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
                case "validate":
                    validated += AddValidationDiagnostics(opEl, diagnostics);
                    break;
                case "addNode":
                case "addPad":
                case "connect":
                case "disconnect":
                case "setBounds":
                case "annotate":
                case "select":
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

        var shouldApply = !dryRun && diagnostics.Count == 0 && unsupported.Count == 0 && plannedSetPins.Count > 0;
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

        var result = new
        {
            ok = diagnostics.Count == 0 && unsupported.Count == 0,
            dryRun,
            label,
            appliedOps = shouldApply ? plannedSetPins.Count : 0,
            checkedOps = plannedSetPins.Count,
            validationChecks = validated,
            unsupported,
            unverified,
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
