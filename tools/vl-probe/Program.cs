using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VlProbe;

/// <summary>
/// Metadata-only API probe for vvvv gamma assemblies.
///
/// Uses MetadataLoadContext so we can run on .NET 10 while inspecting the
/// net8.0 assemblies that ship with vvvv, WITHOUT executing any vvvv code.
/// Goal: dump the real public API surface of VL.Lang / VL.HDE / VL.Core /
/// VL.Core.Commands so the vvvv-agent editor-bridge design can be validated
/// against reality instead of guesses.
/// </summary>
internal static class Program
{
    private static bool _includeNonPublic;

    // Assemblies most relevant to the editor bridge + transactional patch editing.
    private static readonly string[] DefaultTargets =
    {
        "VL.Lang.dll",
        "VL.HDE.dll",
        "VL.Core.dll",
        "VL.Core.Commands.dll",
    };

    // Keywords that flag a type/member as interesting for the agent bridge.
    private static readonly string[] InterestKeywords =
    {
        "Session", "Hovered", "Selected", "Selection",
        "Node", "Pin", "Pad", "Link", "Patch", "Document", "Solution",
        "Command", "Transaction", "Undo", "Edit", "Mutat",
        "Channel", "Diagnostic", "Message", "Error", "Exception",
        "Workspace", "Editor", "Element", "Slot", "Fragment",
    };

    private static int Main(string[] args)
    {
        var installDir = GetArg(args, "--install")
            ?? @"C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64";
        var outDir = GetArg(args, "--out")
            ?? Path.Combine(AppContext.BaseDirectory, "probe-output");
        var targets = GetArg(args, "--targets")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? DefaultTargets;
        var typeFilter = GetArg(args, "--type-filter");
        _includeNonPublic = HasArg(args, "--include-non-public");

        if (!Directory.Exists(installDir))
        {
            Console.Error.WriteLine($"Install dir not found: {installDir}");
            return 1;
        }
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"Install: {installDir}");
        Console.WriteLine($"Output:  {outDir}");
        Console.WriteLine($"Targets: {string.Join(", ", targets)}");
        if (_includeNonPublic) Console.WriteLine("Visibility: all metadata types and members");
        if (!string.IsNullOrWhiteSpace(typeFilter)) Console.WriteLine($"Type filter: {typeFilter}");
        Console.WriteLine();

        // Build the assembly resolution set: every dll under the vvvv install
        // (root wins over nested duplicates) + the running .NET runtime dir so
        // MetadataLoadContext can find a core assembly (System.Private.CoreLib).
        var pathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddDir(string dir, bool recursive)
        {
            if (!Directory.Exists(dir)) return;
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", opt))
            {
                var name = Path.GetFileName(dll);
                // Prefer the first hit (install root is enumerated before deep packs
                // because we add it as a non-recursive pass first).
                pathByName.TryAdd(name, dll);
            }
        }

        AddDir(installDir, recursive: false);            // root: canonical VL.*.dll
        AddDir(RuntimeEnvironment.GetRuntimeDirectory(), recursive: false); // core fx
        AddDir(installDir, recursive: true);             // packs/refs/etc. fallback

        var resolver = new PathAssemblyResolver(pathByName.Values);
        using var mlc = new MetadataLoadContext(resolver, coreAssemblyName: "System.Private.CoreLib");

        var indexLines = new List<string>();
        var fullPath = Path.Combine(outDir, "api-full.md");
        using var full = new StreamWriter(fullPath, append: false, Encoding.UTF8);

        full.WriteLine("# vvvv gamma public API dump (metadata-only)");
        full.WriteLine();
        full.WriteLine($"- Install: `{installDir}`");
        full.WriteLine($"- Assemblies: {string.Join(", ", targets)}");
        full.WriteLine();

        var interesting = new List<(string asm, Type type)>();
        int totalTypes = 0;

        foreach (var target in targets)
        {
            if (!pathByName.TryGetValue(target, out var asmPath))
            {
                Console.Error.WriteLine($"  ! {target} not found in install");
                full.WriteLine($"## {target} — NOT FOUND");
                full.WriteLine();
                continue;
            }

            Assembly asm;
            try
            {
                asm = mlc.LoadFromAssemblyPath(asmPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ! failed to load {target}: {ex.Message}");
                full.WriteLine($"## {target} — LOAD FAILED: {ex.Message}");
                full.WriteLine();
                continue;
            }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t is not null).ToArray()!; }

            var selectedTypes = types
                .Where(t => t is not null && (_includeNonPublic || t.IsPublic || t.IsNestedPublic))
                .Where(t => string.IsNullOrWhiteSpace(typeFilter) || (t.FullName ?? t.Name).Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToArray();

            totalTypes += selectedTypes.Length;
            var visibilityLabel = _includeNonPublic ? "metadata" : "public";
            Console.WriteLine($"  {target}: {selectedTypes.Length} {visibilityLabel} types");
            full.WriteLine($"## {target} ({selectedTypes.Length} {visibilityLabel} types)");
            full.WriteLine();

            foreach (var t in selectedTypes)
            {
                WriteType(full, t);
                if (IsInteresting(t))
                    interesting.Add((target, t));
            }
        }

        // Focused report: only types whose name OR any member name hits a keyword.
        var focusPath = Path.Combine(outDir, "api-bridge-relevant.md");
        using (var focus = new StreamWriter(focusPath, append: false, Encoding.UTF8))
        {
            focus.WriteLine("# vvvv API — editor-bridge-relevant surface");
            focus.WriteLine();
            focus.WriteLine("Types matching agent-bridge keywords (Session, Node, Pin, Command, Channel, …).");
            focus.WriteLine();
            foreach (var (asm, t) in interesting.OrderBy(x => x.asm).ThenBy(x => x.type.FullName, StringComparer.Ordinal))
            {
                focus.WriteLine($"### `{t.FullName}`  _({asm})_");
                focus.WriteLine();
                focus.WriteLine("```csharp");
                WriteTypeSignature(focus, t);
                focus.WriteLine("```");
                focus.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total types: {totalTypes}");
        Console.WriteLine($"Bridge-relevant types: {interesting.Count}");
        Console.WriteLine($"Wrote: {fullPath}");
        Console.WriteLine($"Wrote: {focusPath}");
        return 0;
    }

    private static bool IsInteresting(Type t)
    {
        var name = t.Name;
        if (InterestKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        // Also catch types that merely expose interesting members.
        return SafeMembers(t).Any(m =>
            InterestKeywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    private static void WriteType(TextWriter w, Type t)
    {
        w.WriteLine($"### `{t.FullName}`");
        w.WriteLine();
        w.WriteLine("```csharp");
        WriteTypeSignature(w, t);
        w.WriteLine("```");
        w.WriteLine();
    }

    private static void WriteTypeSignature(TextWriter w, Type t)
    {
        var kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
        w.WriteLine($"public {kind} {Pretty(t)}");
        w.WriteLine("{");

        foreach (var m in SafeMembers(t).OrderBy(m => m.MemberType).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            switch (m)
            {
                case MethodInfo mi when !mi.IsSpecialName:
                    var ps = string.Join(", ", mi.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));
                    w.WriteLine($"    {Pretty(mi.ReturnType)} {mi.Name}({ps});");
                    break;
                case ConstructorInfo ci:
                    var cps = string.Join(", ", ci.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));
                    w.WriteLine($"    {t.Name}({cps});");
                    break;
                case PropertyInfo pi:
                    var acc = (pi.CanRead ? "get; " : "") + (pi.CanWrite ? "set; " : "");
                    w.WriteLine($"    {Pretty(pi.PropertyType)} {pi.Name} {{ {acc}}}");
                    break;
                case FieldInfo fi when fi.IsPublic:
                    w.WriteLine($"    {Pretty(fi.FieldType)} {fi.Name};");
                    break;
                case EventInfo ei:
                    w.WriteLine($"    event {Pretty(ei.EventHandlerType!)} {ei.Name};");
                    break;
            }
        }
        w.WriteLine("}");
    }

    private static IEnumerable<MemberInfo> SafeMembers(Type t)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (_includeNonPublic)
            flags |= BindingFlags.NonPublic;
        try { return t.GetMembers(flags); }
        catch { return Array.Empty<MemberInfo>(); }
    }

    private static string Pretty(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            var args = string.Join(", ", t.GetGenericArguments().Select(Pretty));
            return $"{name}<{args}>";
        }
        return t.Name;
    }

    private static string? GetArg(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasArg(string[] args, string key)
        => args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
}
