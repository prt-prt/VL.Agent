using System.Text.Json;
using System.Text.Json.Serialization;
using VlMap;

// vl-map — static cartographer for a vvvv gamma project.
// Indexes .vl / .cs / .csproj / .sdsl, parses every .vl document, and emits a
// structured JSON index + a human-readable summary. Read-only, no vvvv runtime.

var project = GetArg("--project") ?? Directory.GetCurrentDirectory();
var outPath = GetArg("--out") ?? Path.Combine(project, "vl-map.json");
var quiet = args.Contains("--quiet");

project = Path.GetFullPath(project);
if (!Directory.Exists(project))
{
    Console.Error.WriteLine($"Project dir not found: {project}");
    return 1;
}

var index = new ProjectIndex { Root = project, GeneratedBy = "vl-map" };

// Skip noise dirs while walking.
static bool Skip(string dir)
{
    var n = Path.GetFileName(dir);
    return n is ".git" or "bin" or "obj" or ".vs";
}

var vlFiles = new List<string>();
foreach (var f in EnumerateFiles(project))
{
    switch (Path.GetExtension(f).ToLowerInvariant())
    {
        case ".vl": index.Files.Vl++; vlFiles.Add(f); break;
        case ".cs": index.Files.Cs++; break;
        case ".csproj": index.Files.Csproj++; break;
        case ".sdsl": index.Files.Sdsl++; break;
    }
}

foreach (var f in vlFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    index.Documents.Add(VlParser.Parse(f, project));

// Aggregate package usage across documents (surfaces version drift).
var pkgGroups = index.Documents
    .SelectMany(d => d.Nuget.Select(n => (d, n)))
    .GroupBy(x => x.n.Location, StringComparer.Ordinal)
    .OrderBy(g => g.Key, StringComparer.Ordinal);

foreach (var g in pkgGroups)
{
    var versions = g.Select(x => x.n.Version ?? "?").Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
    var docCount = g.Select(x => x.d).Distinct().Count();
    var use = new PackageUse { Location = g.Key, Versions = versions, DocumentCount = docCount };
    index.Packages.Add(use);
    if (use.VersionConflict)
        index.Warnings.Add($"package '{g.Key}' is pinned to {versions.Count} versions: {string.Join(", ", versions)}");
}

// Document dependency graph.
foreach (var d in index.Documents)
    foreach (var dep in d.Documents)
        index.DocumentEdges.Add(new DocEdge { From = d.Path, To = dep.Resolved ?? dep.Location, Resolved = dep.Exists });

// Bubble up per-document warnings into a project rollup count.
var docsWithWarnings = index.Documents.Count(d => d.Warnings.Count > 0);
var parseFailures = index.Documents.Count(d => d.ParseError is not null);
if (parseFailures > 0)
    index.Warnings.Add($"{parseFailures} document(s) failed to parse");

// Write JSON.
var json = JsonSerializer.Serialize(index, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});
File.WriteAllText(outPath, json);

if (!quiet)
    PrintSummary(index, outPath, docsWithWarnings);

return 0;

// ---- helpers ----

void PrintSummary(ProjectIndex idx, string jsonPath, int docsWithWarnings)
{
    Console.WriteLine($"vl-map  ·  {idx.Root}");
    Console.WriteLine(new string('─', 60));
    Console.WriteLine($"Files: {idx.Files.Vl} .vl · {idx.Files.Cs} .cs · {idx.Files.Csproj} .csproj · {idx.Files.Sdsl} .sdsl");
    Console.WriteLine();

    var totalDefs = idx.Documents.Sum(d => d.Definitions.Count);
    var totalNodes = idx.Documents.Sum(d => d.Stats.Nodes);
    Console.WriteLine($"Documents: {idx.Documents.Count}  ·  Definitions: {totalDefs}  ·  Nodes (total): {totalNodes}");
    Console.WriteLine();

    Console.WriteLine("Largest documents (by node count):");
    foreach (var d in idx.Documents.OrderByDescending(d => d.Stats.Nodes).Take(8))
        Console.WriteLine($"  {d.Stats.Nodes,6} nodes  {d.Definitions.Count,3} defs   {d.Path}");
    Console.WriteLine();

    Console.WriteLine($"Packages referenced: {idx.Packages.Count}");
    var conflicts = idx.Packages.Where(p => p.VersionConflict).ToList();
    if (conflicts.Count > 0)
    {
        Console.WriteLine($"  ⚠ {conflicts.Count} with version drift:");
        foreach (var p in conflicts)
            Console.WriteLine($"     {p.Location}: {string.Join(", ", p.Versions)}");
    }
    Console.WriteLine();

    var unresolved = idx.DocumentEdges.Count(e => !e.Resolved);
    Console.WriteLine($"Document links: {idx.DocumentEdges.Count} ({unresolved} unresolved)");
    Console.WriteLine();

    Console.WriteLine($"Warnings: {idx.Warnings.Count} project-level · {docsWithWarnings} document(s) with warnings");
    foreach (var w in idx.Warnings.Take(12))
        Console.WriteLine($"  • {w}");
    if (idx.Warnings.Count > 12)
        Console.WriteLine($"  … and {idx.Warnings.Count - 12} more (see JSON)");
    Console.WriteLine();
    Console.WriteLine($"Wrote {jsonPath}");
}

static IEnumerable<string> EnumerateFiles(string root)
{
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var dir = stack.Pop();
        string[] subdirs;
        try { subdirs = Directory.GetDirectories(dir); }
        catch { subdirs = []; }
        foreach (var sd in subdirs)
            if (!Skip(sd)) stack.Push(sd);

        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch { files = []; }
        foreach (var f in files) yield return f;
    }
}

string? GetArg(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

