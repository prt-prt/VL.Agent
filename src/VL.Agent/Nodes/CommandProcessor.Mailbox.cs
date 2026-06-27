using System.Text.Json;
using System.Text.Json.Nodes;
using VL.Core;

namespace VL.Agent;

public partial class CommandProcessor
{
    private ProcessResult ProcessFile(string file, string resultsDir)
    {
        var trace = CommandTrace.FromFile(file);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var command = AgentCommand.From(doc.RootElement, file);
            trace = command.Trace;
            trace.PickedUpAtUtc = DateTimeOffset.UtcNow;

            var result = DispatchCommand(command, resultsDir, Path.GetFileName(file));
            return result is null ? ProcessResult.Deferred(trace) : ProcessResult.Now(result, trace);
        }
        catch (Exception ex)
        {
            trace.PickedUpAtUtc ??= DateTimeOffset.UtcNow;
            return ProcessResult.Now(Err($"{Path.GetFileName(file)}: {ex.Message}"), trace);
        }
    }

    private string? DispatchCommand(AgentCommand command, string resultsDir, string requestFileName) =>
        command.Op switch
        {
            "setPinValue" => SetPinValue(command.Payload, _context),
            "openDocument" => OpenDocument(command.Payload),
            "nodeQuery" => NodeQuery(command.Payload),
            "graphTransaction" => GraphTransaction(command.Payload, _context),
            "paste" => Paste(command.Payload, resultsDir, requestFileName, command.Trace),
            _ => Err($"unknown op '{command.Op}'"),
        };

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

    private static string WriteResult(string resultsDir, string requestFileName, string result, CommandTrace trace)
    {
        trace.ProcessedAtUtc ??= DateTimeOffset.UtcNow;
        var finalized = CompleteResult(result, trace, DateTimeOffset.UtcNow);
        try
        {
            var path = Path.Combine(resultsDir, requestFileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, finalized);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort */ }
        return finalized;
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string Ok(string msg) => "{\"ok\":true,\"message\":" + JsonSerializer.Serialize(msg) + "}";
    private static string Err(string msg) => "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(msg) + "}";

    private static string CompleteResult(string result, CommandTrace trace, DateTimeOffset resultWrittenAtUtc)
    {
        trace.ProcessedAtUtc ??= DateTimeOffset.UtcNow;
        if (resultWrittenAtUtc < trace.ProcessedAtUtc.Value)
            resultWrittenAtUtc = trace.ProcessedAtUtc.Value;
        trace.ResultWrittenAtUtc = resultWrittenAtUtc;

        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(result) as JsonObject ?? new JsonObject { ["ok"] = false, ["rawResult"] = result };
        }
        catch
        {
            obj = new JsonObject { ["ok"] = false, ["rawResult"] = result };
        }

        obj["requestId"] = trace.RequestId;
        obj["traceId"] = trace.TraceId;
        obj["op"] = trace.Op;
        obj["trace"] = trace.ToJson();
        return obj.ToJsonString();
    }

    private readonly record struct ProcessResult(string? ImmediateResult, CommandTrace Trace)
    {
        public static ProcessResult Now(string result, CommandTrace trace) => new(result, trace);
        public static ProcessResult Deferred(CommandTrace trace) => new(null, trace);
    }

    private sealed class AgentCommand
    {
        public required string Op { get; init; }
        public required JsonElement Payload { get; init; }
        public required CommandTrace Trace { get; init; }

        public static AgentCommand From(JsonElement root, string file)
        {
            var requestFileName = Path.GetFileName(file);
            var requestId = GetString(root, "requestId") ?? Path.GetFileNameWithoutExtension(file);
            var traceId = GetString(root, "traceId") ?? requestId;
            var op = GetString(root, "op") ?? "setPinValue";
            var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaEl) && schemaEl.ValueKind == JsonValueKind.Number
                ? schemaEl.GetInt32()
                : (int?)null;
            var transport = GetString(root, "transport") ?? "fileMailbox";
            var deadlineMs = root.TryGetProperty("deadlineMs", out var deadlineEl) && deadlineEl.ValueKind == JsonValueKind.Number
                ? deadlineEl.GetInt32()
                : (int?)null;
            var createdAtUtc = TryReadTime(root, "createdAtUtc") ?? TryReadFileTime(file);
            var isEnvelope = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object;
            if (!isEnvelope)
                payload = root;

            return new AgentCommand
            {
                Op = op,
                Payload = payload,
                Trace = new CommandTrace
                {
                    RequestId = requestId,
                    TraceId = traceId,
                    Op = op,
                    Transport = transport,
                    RequestFileName = requestFileName,
                    SchemaVersion = schemaVersion,
                    DeadlineMs = deadlineMs,
                    CreatedAtUtc = createdAtUtc,
                    Envelope = isEnvelope,
                },
            };
        }
    }

    private sealed class CommandTrace
    {
        public required string RequestId { get; init; }
        public required string TraceId { get; init; }
        public required string Op { get; init; }
        public required string Transport { get; init; }
        public required string RequestFileName { get; init; }
        public int? SchemaVersion { get; init; }
        public int? DeadlineMs { get; init; }
        public bool Envelope { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset? PickedUpAtUtc { get; set; }
        public DateTimeOffset? ProcessedAtUtc { get; set; }
        public DateTimeOffset? ResultWrittenAtUtc { get; set; }

        public static CommandTrace FromFile(string file)
        {
            var requestId = Path.GetFileNameWithoutExtension(file);
            return new CommandTrace
            {
                RequestId = requestId,
                TraceId = requestId,
                Op = "unknown",
                Transport = "fileMailbox",
                RequestFileName = Path.GetFileName(file),
                CreatedAtUtc = TryReadFileTime(file),
                Envelope = false,
            };
        }

        public JsonObject ToJson()
        {
            var pickedUp = PickedUpAtUtc;
            var processed = ProcessedAtUtc;
            var written = ResultWrittenAtUtc;
            var obj = new JsonObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["envelope"] = Envelope,
                ["transport"] = Transport,
                ["requestFileName"] = RequestFileName,
                ["deadlineMs"] = DeadlineMs,
                ["createdAtUtc"] = CreatedAtUtc.ToString("O"),
                ["pickedUpAtUtc"] = pickedUp?.ToString("O"),
                ["processedAtUtc"] = processed?.ToString("O"),
                ["resultWrittenAtUtc"] = written?.ToString("O"),
                ["mailboxWaitMs"] = pickedUp is null ? null : (int)Math.Max(0, (pickedUp.Value - CreatedAtUtc).TotalMilliseconds),
                ["processingMs"] = pickedUp is null || processed is null ? null : (int)Math.Max(0, (processed.Value - pickedUp.Value).TotalMilliseconds),
                ["resultWriteDelayMs"] = processed is null || written is null ? null : (int)Math.Max(0, (written.Value - processed.Value).TotalMilliseconds),
                ["roundTripMs"] = written is null ? null : (int)Math.Max(0, (written.Value - CreatedAtUtc).TotalMilliseconds),
            };
            return obj;
        }
    }

    private static DateTimeOffset? TryReadTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(el.GetString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset TryReadFileTime(string file)
    {
        try { return File.GetCreationTimeUtc(file); }
        catch { return DateTimeOffset.UtcNow; }
    }
}
