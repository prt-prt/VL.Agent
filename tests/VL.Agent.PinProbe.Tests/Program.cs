using System.Text.Json;
using System.Text.Json.Nodes;
using VL.Agent;

static PinSample Sample(
    string element,
    string pin,
    string? value,
    string? name = null,
    string kind = "node",
    string? type = "Single",
    bool isInput = false,
    bool isConnected = true)
    => new(element, name ?? element, kind, pin, type, isInput, isConnected, value);

static PinProbeOptions Options(
    int frames = 4,
    string[]? pins = null,
    string[]? targets = null,
    int maxValueLength = 200,
    int maxSamples = 16)
    => new(frames, pins, targets, maxValueLength, maxSamples);

static PinProbeObservation Run(PinProbeOptions options, params PinSample[][] frames)
{
    var sampler = new PinProbeSampler(options);
    var observation = default(PinProbeObservation);
    foreach (var frame in frames)
        observation = sampler.Observe(frame);
    return observation;
}

static PinProbeTrack Pin(PinProbeObservation observation, string pinName)
{
    foreach (var track in observation.Pins)
        if (track.PinName == pinName)
            return track;
    throw new InvalidOperationException($"no track for pin '{pinName}'");
}

static (bool Ok, PinProbeOptions? Options, string Error) ReadOptions(string json)
{
    using var doc = JsonDocument.Parse(json);
    var ok = PinProbe.TryReadOptions(doc.RootElement, out var options, out var error);
    return (ok, ok ? options : null, error);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

// 1. A completed window reports first, last, counts, and changed.
{
    var result = Run(
        Options(frames: 3),
        [Sample("a", "Output", "1")],
        [Sample("a", "Output", "2")],
        [Sample("a", "Output", "2")]);

    Assert(result.Status == PinProbeStatus.Completed, "window should complete on the requested frame");
    Assert(result.Frames == 3, "frame count should be reported");
    var pin = Pin(result, "Output");
    Assert(pin.First == "1", "first value should be the earliest sample");
    Assert(pin.Last == "2", "last value should be the latest sample");
    Assert(pin.SampleCount == 3, "every frame should be counted");
    Assert(pin.DistinctCount == 2, "distinct values should be counted");
    Assert(pin.Changed, "a pin with more than one distinct value has changed");
}

// A pin holding one value across the window is reported as unchanged.
{
    var result = Run(
        Options(frames: 2),
        [Sample("a", "Value", "0.5")],
        [Sample("a", "Value", "0.5")]);

    var pin = Pin(result, "Value");
    Assert(!pin.Changed, "a constant pin must not be reported as changed");
    Assert(pin.DistinctCount == 1, "a constant pin has one distinct value");
}

// 2. Numeric windows report min/max; any non-numeric sample disables them.
{
    var result = Run(
        Options(frames: 3),
        [Sample("a", "Output", "0.25")],
        [Sample("a", "Output", "-1.5")],
        [Sample("a", "Output", "2")]);

    var pin = Pin(result, "Output");
    Assert(pin.Numeric, "all-parseable samples should report numeric");
    Assert(pin.Min == -1.5, "min should be the smallest sample");
    Assert(pin.Max == 2, "max should be the largest sample");
}

{
    var result = Run(
        Options(frames: 2),
        [Sample("a", "Output", "1")],
        [Sample("a", "Output", "<spread>")]);

    var pin = Pin(result, "Output");
    Assert(!pin.Numeric, "a non-numeric sample must disable numeric reporting");
    Assert(pin.Min is null && pin.Max is null, "non-numeric pins must not report a range");
}

// NaN is not a usable range bound, but stays visible in the samples.
{
    var result = Run(
        Options(frames: 2),
        [Sample("a", "Output", "NaN")],
        [Sample("a", "Output", "NaN")]);

    var pin = Pin(result, "Output");
    Assert(!pin.Numeric, "NaN must not be reported as a numeric range");
    Assert(pin.Last == "NaN", "NaN must remain visible in the sampled values");
}

// A pin that never yields a value is honest about it rather than guessing.
{
    var result = Run(
        Options(frames: 2),
        [Sample("a", "Output", null)],
        [Sample("a", "Output", null)]);

    var pin = Pin(result, "Output");
    Assert(!pin.Numeric, "unreadable values must not be numeric");
    Assert(pin.Last is null, "unreadable values stay null");
    Assert(pin.SampleCount == 2, "unreadable frames are still counted");
}

// 3. Retained samples are bounded while counts stay accurate for the full window.
{
    var result = Run(
        Options(frames: 4, maxSamples: 2),
        [Sample("a", "Output", "1")],
        [Sample("a", "Output", "2")],
        [Sample("a", "Output", "3")],
        [Sample("a", "Output", "4")]);

    var pin = Pin(result, "Output");
    Assert(pin.Samples.Length == 2, "retained samples must respect maxSamples");
    Assert(pin.SamplesTruncated, "dropping samples must be flagged");
    Assert(pin.SampleCount == 4, "sampleCount must cover the whole window");
    Assert(pin.DistinctCount == 4, "distinctCount must cover the whole window");
    Assert(pin.Last == "4", "last must come from the final frame, not the retained samples");
}

// 4. Long values are truncated with a visible marker.
{
    var long_ = new string('x', 40);
    var formatted = PinProbe.FormatValue(long_, 16);
    Assert(formatted == new string('x', 16) + "…+24 chars", "truncation should mark the dropped length");
    Assert(PinProbe.FormatValue("short", 16) == "short", "short values pass through untouched");
    Assert(PinProbe.FormatValue(null, 16) is null, "null stays null");

    var result = Run(
        Options(frames: 1, maxValueLength: 16),
        [Sample("a", "Output", long_)]);
    Assert(Pin(result, "Output").Last!.EndsWith("…+24 chars", StringComparison.Ordinal),
        "sampled values should be truncated through the options");
}

// 5. Pin and target filters are honored.
{
    var result = Run(
        Options(frames: 1, pins: ["output"]),
        [Sample("a", "Output", "1"), Sample("a", "Input", "2")]);

    Assert(result.Pins.Length == 1, "pin filter should drop unmatched pins");
    Assert(result.Pins[0].PinName == "Output", "pin filter should be case-insensitive");
}

{
    var result = Run(
        Options(frames: 1, targets: ["b"]),
        [Sample("a", "Output", "1"), Sample("b", "Output", "2")]);

    Assert(result.Pins.Length == 1, "target filter should drop unmatched elements");
    Assert(result.Pins[0].ElementUniqueId == "b", "target filter should match by uniqueId");
}

{
    var result = Run(
        Options(frames: 1, targets: ["lfo"]),
        [Sample("id-1", "Output", "1", name: "LFO"), Sample("id-2", "Output", "2", name: "Circle")]);

    Assert(result.Pins.Length == 1, "target filter should match element names too");
    Assert(result.Pins[0].ElementUniqueId == "id-1", "name match should select the right element");
}

// 6. Pins appearing or disappearing mid-window are kept with an honest count.
{
    var result = Run(
        Options(frames: 4),
        [Sample("a", "Early", "1")],
        [Sample("a", "Early", "1")],
        [Sample("a", "Early", "1"), Sample("a", "Late", "9")],
        [Sample("a", "Early", "1"), Sample("a", "Late", "9")]);

    Assert(result.Status == PinProbeStatus.Completed, "late pins must not prevent completion");
    Assert(Pin(result, "Early").SampleCount == 4, "the pin present all window should count 4");
    Assert(Pin(result, "Late").SampleCount == 2, "the late pin should count only its own frames");
}

{
    var result = Run(
        Options(frames: 3),
        [Sample("a", "Gone", "1"), Sample("a", "Stays", "1")],
        [Sample("a", "Stays", "1")],
        [Sample("a", "Stays", "1")]);

    Assert(Pin(result, "Gone").SampleCount == 1, "a disappearing pin keeps the frames it had");
    Assert(Pin(result, "Stays").SampleCount == 3, "a surviving pin is unaffected");
}

// 7. An empty first frame fails fast instead of burning the window.
{
    var sampler = new PinProbeSampler(Options(frames: 30));
    var result = sampler.Observe([]);
    Assert(result.Status == PinProbeStatus.NoPins, "an empty selection should report noPins immediately");
    Assert(result.Frames == 1, "noPins should be reported on the first frame");
}

// A window that is filtered down to nothing is noPins, not an empty success.
{
    var sampler = new PinProbeSampler(Options(frames: 4, pins: ["Missing"]));
    var result = sampler.Observe([Sample("a", "Output", "1")]);
    Assert(result.Status == PinProbeStatus.NoPins, "filtering everything out must not look like success");
}

// 8. noPins never serializes as a success.
{
    var json = PinProbe.BuildResult(new PinProbeObservation(PinProbeStatus.NoPins, 1, []), "selection");
    Assert((bool)json["ok"]! == false, "noPins must not report ok");
    Assert((string?)json["status"] == "noPins", "status should be noPins");
    Assert(!string.IsNullOrWhiteSpace((string?)json["error"]), "noPins must explain itself");
    Assert((int)json["pinCount"]! == 0, "noPins has no pins");
}

// An incomplete window is a failure too, never an empty success.
{
    var json = PinProbe.BuildResult(new PinProbeObservation(PinProbeStatus.Pending, 3, []), "selection");
    Assert((bool)json["ok"]! == false, "a pending window must not report ok");
    Assert((string?)json["status"] == "pending", "status should stay pending");
    Assert((int)json["pinCount"]! == 0, "a pending window reports no pins");
}

// Result shaping groups pins under one element even when a pin appears late.
{
    var observation = Run(
        Options(frames: 3),
        [Sample("a", "Early", "1", name: "LFO"), Sample("b", "Other", "1", name: "Circle")],
        [Sample("a", "Early", "2", name: "LFO"), Sample("b", "Other", "1", name: "Circle")],
        [Sample("a", "Early", "3", name: "LFO"), Sample("b", "Other", "1", name: "Circle"),
         Sample("a", "Late", "7", name: "LFO")]);

    var json = PinProbe.BuildResult(observation, "selection");
    Assert((bool)json["ok"]!, "a completed window should report ok");
    Assert((string?)json["source"] == "selection", "source should be reported");
    Assert((int)json["elementCount"]! == 2, "two elements were probed");
    Assert((int)json["pinCount"]! == 3, "three pins were probed");

    var elements = (JsonArray)json["elements"]!;
    Assert(elements.Count == 2, "a late pin must not split its element into two entries");

    var first = elements[0]!;
    Assert((string?)first["uniqueId"] == "a", "first-seen element order should be preserved");
    Assert((string?)first["name"] == "LFO", "element name should be reported");
    Assert(((JsonArray)first["pins"]!).Count == 2, "both of the element's pins should be grouped");

    var earlyPin = ((JsonArray)first["pins"]!)[0]!;
    Assert((string?)earlyPin["name"] == "Early", "pin name should be reported");
    Assert((bool)earlyPin["changed"]!, "a moving pin should report changed");
    Assert((bool)earlyPin["numeric"]!, "a numeric pin should report a range");
    Assert((double)earlyPin["min"]! == 1 && (double)earlyPin["max"]! == 3, "range should span the window");
}

// Options are clamped to the documented bounds.
{
    var wide = Options(frames: 9999, maxValueLength: 1, maxSamples: 9999);
    Assert(wide.Frames == 120, "frames should clamp to 120");
    Assert(wide.MaxValueLength == 16, "maxValueLength should clamp up to 16");
    Assert(wide.MaxSamples == 120, "maxSamples should clamp to 120");

    var narrow = Options(frames: 0, maxValueLength: 999999, maxSamples: 0);
    Assert(narrow.Frames == 1, "frames should clamp up to 1");
    Assert(narrow.MaxValueLength == 4096, "maxValueLength should clamp down to 4096");
    Assert(narrow.MaxSamples == 1, "maxSamples should clamp up to 1");
}

// Option reading: defaults, filters, and actionable rejections.
{
    var (ok, options, _) = ReadOptions("{}");
    Assert(ok, "an empty payload should be valid");
    Assert(options!.Frames == PinProbe.DefaultFrames, "frames should default");
    Assert(options.MaxValueLength == PinProbe.DefaultMaxValueLength, "maxValueLength should default");
    Assert(options.MaxSamples == PinProbe.DefaultMaxSamples, "maxSamples should default");
    Assert(options.PinNames.Length == 0 && options.Targets.Length == 0, "filters default to empty");
}

{
    var (ok, options, _) = ReadOptions(
        """{"frames":12,"pins":[" Output "],"targets":["LFO"],"maxValueLength":32,"maxSamples":4}""");
    Assert(ok, "a full payload should be valid");
    Assert(options!.Frames == 12, "frames should be read");
    Assert(options.MaxValueLength == 32, "maxValueLength should be read");
    Assert(options.MaxSamples == 4, "maxSamples should be read");
    Assert(options.PinNames is ["Output"], "pin filter entries should be trimmed");
    Assert(options.Targets is ["LFO"], "target filter should be read");
}

{
    var (ok, options, _) = ReadOptions("""{"frames":null,"pins":null}""");
    Assert(ok, "explicit nulls should fall back to defaults");
    Assert(options!.Frames == PinProbe.DefaultFrames, "null frames should default");
}

{
    var (ok, _, error) = ReadOptions("""{"frames":500}""");
    Assert(!ok, "out-of-range frames should be rejected, not clamped");
    Assert(error.Contains("between 1 and 120", StringComparison.Ordinal), "the range should be stated: " + error);
}

{
    var (ok, _, error) = ReadOptions("""{"frames":"eight"}""");
    Assert(!ok, "a non-numeric frames should be rejected");
    Assert(error.Contains("integer", StringComparison.Ordinal), "the expected kind should be stated: " + error);
}

{
    var (ok, _, error) = ReadOptions("""{"maxValueLength":4}""");
    Assert(!ok, "too small a maxValueLength should be rejected");
    Assert(error.Contains("maxValueLength", StringComparison.Ordinal), "the field should be named: " + error);
}

{
    var (ok, _, error) = ReadOptions("""{"maxSamples":0}""");
    Assert(!ok, "a zero maxSamples should be rejected");
    Assert(error.Contains("maxSamples", StringComparison.Ordinal), "the field should be named: " + error);
}

{
    var (ok, _, error) = ReadOptions("""{"pins":"Output"}""");
    Assert(!ok, "a scalar pins filter should be rejected");
    Assert(error.Contains("array of strings", StringComparison.Ordinal), "the expected shape should be stated: " + error);
}

{
    var (ok, _, error) = ReadOptions("""{"targets":[1]}""");
    Assert(!ok, "non-string target entries should be rejected");
    Assert(error.Contains("array of strings", StringComparison.Ordinal), "the expected shape should be stated: " + error);
}

{
    var (ok, _, error) = ReadOptions("""{"targets":["  "]}""");
    Assert(!ok, "a blank target must not silently widen the probe to everything");
    Assert(error.Contains("non-empty", StringComparison.Ordinal), "the reason should be stated: " + error);
}

Console.WriteLine("pin probe sampler: ok");
