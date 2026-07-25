using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VL.Agent;

/// <summary>One live pin reading taken on a single vvvv frame.</summary>
internal readonly record struct PinSample(
    string ElementUniqueId,
    string? ElementName,
    string ElementKind,
    string PinName,
    string? PinType,
    bool IsInput,
    bool IsConnected,
    string? Value);

internal enum PinProbeStatus
{
    Pending,
    Completed,
    NoPins,
}

/// <summary>What one pin did across the sampling window.</summary>
internal readonly record struct PinProbeTrack(
    string ElementUniqueId,
    string? ElementName,
    string ElementKind,
    string PinName,
    string? PinType,
    bool IsInput,
    bool IsConnected,
    string? First,
    string? Last,
    int SampleCount,
    int DistinctCount,
    bool Changed,
    bool Numeric,
    double? Min,
    double? Max,
    string?[] Samples,
    bool SamplesTruncated);

internal readonly record struct PinProbeObservation(
    PinProbeStatus Status,
    int Frames,
    PinProbeTrack[] Pins);

internal sealed class PinProbeOptions
{
    public PinProbeOptions(
        int frames,
        string[]? pinNames,
        string[]? targets,
        int maxValueLength,
        int maxSamples)
    {
        Frames = Math.Clamp(frames, 1, 120);
        MaxValueLength = Math.Clamp(maxValueLength, 16, 4096);
        MaxSamples = Math.Clamp(maxSamples, 1, 120);
        PinNames = pinNames ?? [];
        Targets = targets ?? [];
    }

    public int Frames { get; }
    public int MaxValueLength { get; }
    public int MaxSamples { get; }
    public string[] PinNames { get; }
    public string[] Targets { get; }

    public bool Includes(PinSample sample)
        => MatchesPin(sample.PinName) && MatchesTarget(sample);

    private bool MatchesPin(string pinName)
    {
        if (PinNames.Length == 0) return true;
        foreach (var candidate in PinNames)
            if (string.Equals(candidate, pinName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool MatchesTarget(PinSample sample)
    {
        if (Targets.Length == 0) return true;
        foreach (var candidate in Targets)
        {
            if (string.Equals(candidate, sample.ElementUniqueId, StringComparison.Ordinal))
                return true;
            if (sample.ElementName is not null
                && string.Equals(candidate, sample.ElementName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Pure multi-frame accumulator for live pin values. One instant cannot tell a moving
/// value from a stuck one, so the probe records the shape of a window: first, last,
/// distinct count, and numeric range. Holds no vvvv types.
/// </summary>
internal sealed class PinProbeSampler
{
    private readonly PinProbeOptions _options;
    private readonly Dictionary<(string Element, string Pin), Track> _tracks = [];
    private readonly List<(string Element, string Pin)> _order = [];
    private int _frames;

    public PinProbeSampler(PinProbeOptions options) => _options = options;

    public PinProbeObservation Observe(IReadOnlyList<PinSample> samples)
    {
        _frames++;

        foreach (var sample in samples)
        {
            if (!_options.Includes(sample)) continue;

            var key = (sample.ElementUniqueId, sample.PinName);
            if (!_tracks.TryGetValue(key, out var track))
            {
                track = new Track(sample);
                _tracks[key] = track;
                _order.Add(key);
            }

            track.Add(PinProbe.FormatValue(sample.Value, _options.MaxValueLength), _options.MaxSamples);
        }

        // Nothing to watch: say so on the first frame instead of burning the window.
        if (_tracks.Count == 0)
            return new PinProbeObservation(PinProbeStatus.NoPins, _frames, []);

        return _frames >= _options.Frames
            ? new PinProbeObservation(PinProbeStatus.Completed, _frames, BuildTracks())
            : new PinProbeObservation(PinProbeStatus.Pending, _frames, []);
    }

    private PinProbeTrack[] BuildTracks()
    {
        var result = new PinProbeTrack[_order.Count];
        for (var i = 0; i < _order.Count; i++)
            result[i] = _tracks[_order[i]].ToTrack();
        return result;
    }

    private sealed class Track
    {
        private readonly PinSample _first;
        private readonly List<string?> _samples = [];
        private readonly HashSet<string?> _distinct = [];
        private string? _last;
        private int _count;
        private bool _truncated;
        private bool _numeric = true;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;

        public Track(PinSample first) => _first = first;

        public void Add(string? value, int maxSamples)
        {
            if (_count == 0) FirstValue = value;
            _last = value;
            _count++;
            _distinct.Add(value);

            if (_samples.Count < maxSamples) _samples.Add(value);
            else _truncated = true;

            if (value is not null
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && !double.IsNaN(number))
            {
                if (number < _min) _min = number;
                if (number > _max) _max = number;
            }
            else
            {
                _numeric = false;
            }
        }

        private string? FirstValue { get; set; }

        public PinProbeTrack ToTrack()
        {
            var numeric = _numeric && _count > 0;
            return new PinProbeTrack(
                _first.ElementUniqueId,
                _first.ElementName,
                _first.ElementKind,
                _first.PinName,
                _first.PinType,
                _first.IsInput,
                _first.IsConnected,
                FirstValue,
                _last,
                _count,
                _distinct.Count,
                _distinct.Count > 1,
                numeric,
                numeric ? _min : null,
                numeric ? _max : null,
                [.. _samples],
                _truncated);
        }
    }
}

internal static class PinProbe
{
    public const int DefaultFrames = 8;
    public const int DefaultMaxValueLength = 200;
    public const int DefaultMaxSamples = 16;

    public const string NoPinsError =
        "no probeable pins; select a node or pad in the vvvv editor, "
      + "or pass targets that match the current selection";

    /// <summary>
    /// Reads probe options from a request payload. Out-of-range values are rejected
    /// rather than silently clamped, so a mistaken request produces an actionable
    /// error instead of a window the caller did not ask for.
    /// </summary>
    public static bool TryReadOptions(JsonElement payload, out PinProbeOptions options, out string error)
    {
        options = default!;

        if (!TryReadInt(payload, "frames", DefaultFrames, out var frames, out error))
            return false;
        if (frames is < 1 or > 120)
        {
            error = "frames must be between 1 and 120";
            return false;
        }

        if (!TryReadInt(payload, "maxValueLength", DefaultMaxValueLength, out var maxValueLength, out error))
            return false;
        if (maxValueLength is < 16 or > 4096)
        {
            error = "maxValueLength must be between 16 and 4096";
            return false;
        }

        if (!TryReadInt(payload, "maxSamples", DefaultMaxSamples, out var maxSamples, out error))
            return false;
        if (maxSamples is < 1 or > 120)
        {
            error = "maxSamples must be between 1 and 120";
            return false;
        }

        if (!TryReadStrings(payload, "pins", out var pins, out error)) return false;
        if (!TryReadStrings(payload, "targets", out var targets, out error)) return false;

        options = new PinProbeOptions(frames, pins, targets, maxValueLength, maxSamples);
        return true;
    }

    private static bool TryReadInt(
        JsonElement payload,
        string name,
        int fallback,
        out int value,
        out string error)
    {
        error = "";
        value = fallback;
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            error = name + " must be an integer";
            return false;
        }
        return true;
    }

    private static bool TryReadStrings(
        JsonElement payload,
        string name,
        out string[] values,
        out string error)
    {
        error = "";
        values = [];
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            error = name + " must be an array of strings";
            return false;
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = name + " must be an array of strings";
                return false;
            }

            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = name + " entries must be non-empty";
                return false;
            }

            result.Add(text.Trim());
        }

        values = [.. result];
        return true;
    }

    /// <summary>
    /// Reads a live value as text. Live pin reads can throw for an unevaluated or faulted
    /// element, and an unreadable pin is data the caller needs rather than a failure.
    /// </summary>
    public static string? SafeText(Func<object?> read)
    {
        try { return read()?.ToString(); }
        catch { return null; }
    }

    /// <summary>Caps one sample so a large spread cannot dominate the result.</summary>
    public static string? FormatValue(string? raw, int maxValueLength)
    {
        if (raw is null) return null;
        if (raw.Length <= maxValueLength) return raw;
        return raw[..maxValueLength] + "…+" + (raw.Length - maxValueLength) + " chars";
    }

    public static JsonObject BuildResult(PinProbeObservation observation, string source)
    {
        // Anything short of a completed window is a failure, never an empty success: a
        // probe that reports ok with no pins would read as "nothing is wrong".
        if (observation.Status != PinProbeStatus.Completed)
        {
            var noPins = observation.Status == PinProbeStatus.NoPins;
            return new JsonObject
            {
                ["ok"] = false,
                ["status"] = noPins ? "noPins" : "pending",
                ["source"] = source,
                ["frames"] = observation.Frames,
                ["elementCount"] = 0,
                ["pinCount"] = 0,
                ["error"] = noPins ? NoPinsError : "pin probe window did not complete",
            };
        }

        // Group by element without assuming contiguity: a pin can first appear on a
        // later frame, which would otherwise split its element into two entries.
        var pins = observation.Pins;
        var grouped = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var elements = new JsonArray();

        foreach (var track in pins)
        {
            if (!grouped.TryGetValue(track.ElementUniqueId, out var element))
            {
                element = new JsonObject
                {
                    ["uniqueId"] = track.ElementUniqueId,
                    ["name"] = track.ElementName,
                    ["kind"] = track.ElementKind,
                    ["pins"] = new JsonArray(),
                };
                grouped[track.ElementUniqueId] = element;
                elements.Add(element);
            }

            ((JsonArray)element["pins"]!).Add(BuildPin(track));
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["status"] = "completed",
            ["source"] = source,
            ["frames"] = observation.Frames,
            ["elementCount"] = grouped.Count,
            ["pinCount"] = pins.Length,
            ["elements"] = elements,
        };
    }

    private static JsonObject BuildPin(PinProbeTrack track)
    {
        var samples = new JsonArray();
        foreach (var sample in track.Samples)
            samples.Add(sample is null ? null : JsonValue.Create(sample));

        return new JsonObject
        {
            ["name"] = track.PinName,
            ["type"] = track.PinType,
            ["isInput"] = track.IsInput,
            ["isConnected"] = track.IsConnected,
            ["first"] = track.First,
            ["last"] = track.Last,
            ["sampleCount"] = track.SampleCount,
            ["distinctCount"] = track.DistinctCount,
            ["changed"] = track.Changed,
            ["numeric"] = track.Numeric,
            ["min"] = track.Min,
            ["max"] = track.Max,
            ["samples"] = samples,
            ["samplesTruncated"] = track.SamplesTruncated,
        };
    }
}
