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
    private static bool TryGetActiveCanvas(out Canvas canvas, out string error)
    {
        canvas = default!;
        error = "";

        try
        {
            var liveCanvas = API.ActiveLiveCanvasStream?.Value;
            if (liveCanvas?.Canvas is null)
            {
                error = "no active live canvas; focus a patch canvas in the vvvv editor";
                return false;
            }

            canvas = liveCanvas.Canvas;
            return true;
        }
        catch (Exception ex)
        {
            error = "could not read active live canvas: " + ex.Message;
            return false;
        }
    }

    private static bool TryMapPadType(string type, out TypeReference typeReference, out Type clrType)
    {
        switch (type.Trim().ToLowerInvariant())
        {
            case "bool":
            case "boolean":
                typeReference = TypeReference.BooleanRef;
                clrType = typeof(bool);
                return true;
            case "int":
            case "int32":
            case "integer":
            case "integer32":
                typeReference = TypeReference.Integer32Ref;
                clrType = typeof(int);
                return true;
            case "float":
            case "single":
            case "float32":
                typeReference = TypeReference.Float32Ref;
                clrType = typeof(float);
                return true;
            case "double":
            case "float64":
                typeReference = TypeReference.Float64Ref;
                clrType = typeof(double);
                return true;
            case "string":
                typeReference = TypeReference.StringRef;
                clrType = typeof(string);
                return true;
            default:
                typeReference = default!;
                clrType = default!;
                return false;
        }
    }

    private static object DefaultValueFor(Type clrType)
        => clrType == typeof(string) ? ""
         : clrType == typeof(bool) ? false
         : clrType == typeof(float) ? 0f
         : clrType == typeof(double) ? 0d
         : clrType == typeof(int) ? 0
         : "";

    // Experimental: direct SessionNodes.Paste from this node's Update was observed
    // to race the graphical editor render pass. This path requires an explicit
    // opt-in and posts the mutation to the UI synchronization context so it runs
    // after the current Update call returns. Keep this behind MCP/dev tooling until
    // repeated in-vvvv testing proves it stable.
    private static string? Paste(JsonElement root, string resultsDir, string requestFileName, CommandTrace trace)
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
            WriteResult(resultsDir, requestFileName, result, trace);
        }, null);

        return null;
    }

    private static int _pendingPastes;
}
