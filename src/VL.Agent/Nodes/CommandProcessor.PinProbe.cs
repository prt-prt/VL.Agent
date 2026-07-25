using System.Text.Json;
using VL.HDE;
using VL.Lang.PublicAPI;

namespace VL.Agent;

public partial class CommandProcessor
{
    private const string PinProbeSource = "selection";

    private PendingPinProbe? _pendingPinProbe;

    private bool HasPendingPinProbe => _pendingPinProbe is not null;

    /// <summary>
    /// Read-only multi-frame probe of live pin values on the current selection. Defers
    /// its result across frames like a verified transaction; never mutates the solution.
    /// </summary>
    private string? ProbePins(
        JsonElement payload,
        string resultsDir,
        string requestFileName,
        CommandTrace trace)
    {
        if (!PinProbe.TryReadOptions(payload, out var options, out var error))
            return Err(error);

        if (_pendingPinProbe is not null)
            return Err("a pin probe is already pending");

        // Sample frame one now so an empty or over-filtered selection fails immediately
        // instead of burning the whole window.
        var sampler = new PinProbeSampler(options);
        var observation = sampler.Observe(CollectSelectionSamples());
        if (observation.Status != PinProbeStatus.Pending)
            return PinProbe.BuildResult(observation, PinProbeSource).ToJsonString();

        _pendingPinProbe = new PendingPinProbe(resultsDir, requestFileName, trace, sampler);
        return null;
    }

    /// <summary>Advances at most one pin probe once per vvvv frame.</summary>
    private string? TickPendingPinProbe()
    {
        var pending = _pendingPinProbe;
        if (pending is null)
            return null;

        var observation = pending.Sampler.Observe(CollectSelectionSamples());
        if (observation.Status == PinProbeStatus.Pending)
            return null;

        var written = WriteResult(
            pending.ResultsDir,
            pending.RequestFileName,
            PinProbe.BuildResult(observation, PinProbeSource).ToJsonString(),
            pending.Trace);
        _pendingPinProbe = null;
        return written;
    }

    /// <summary>
    /// Reads one frame of live pin values from the current editor selection. This is the
    /// only live-element lookup the public API offers; see docs/sprint-03-pin-value-probe.md
    /// for the whole-canvas question that is still open.
    /// </summary>
    private static PinSample[] CollectSelectionSamples()
    {
        var samples = new List<PinSample>();
        try
        {
            var selection = API.CurrentSelection?.Value;
            if (selection is null)
                return [];

            foreach (var item in selection)
            {
                if (item is not ILiveElement live)
                    continue;

                var element = live.Element;
                if (element is null)
                    continue;

                var uniqueId = element.UniqueId.ToString();
                var elementName = live.Info?.ElementName;

                if (item is ILiveNodeApplication node)
                {
                    foreach (var pin in node.Pins)
                        AddPinSample(samples, uniqueId, elementName, "node", pin);
                }
                else if (item is ILiveDataHub hub)
                {
                    // A pad exposes one value; "Value" matches the pin name the MCP-side
                    // static validation already expects for pads.
                    AddPinSample(samples, uniqueId, elementName, "pad", hub, "Value");
                }
            }
        }
        catch
        {
            // Selection shape is editor-owned; keep whatever was read rather than failing.
        }

        return [.. samples];
    }

    private static void AddPinSample(
        List<PinSample> samples,
        string uniqueId,
        string? elementName,
        string kind,
        ILiveDataHub pin,
        string? nameOverride = null)
    {
        try
        {
            var info = pin.Info;
            var name = nameOverride ?? info.Name;
            if (string.IsNullOrWhiteSpace(name))
                return;

            samples.Add(new PinSample(
                uniqueId,
                elementName,
                kind,
                name,
                PinProbe.SafeText(() => info.CompiletimeType),
                info.IsInput,
                pin.IsConnected,
                PinProbe.SafeText(() => info.Value)));
        }
        catch
        {
            // One unreadable pin must not lose the rest of the frame.
        }
    }

    private sealed record PendingPinProbe(
        string ResultsDir,
        string RequestFileName,
        CommandTrace Trace,
        PinProbeSampler Sampler);
}
