using System.Text.Json;
using System.Text.Json.Serialization;
using VlMap;

// vl-map — static cartographer for a vvvv gamma project.
// Indexes .vl / .cs / .csproj / .sdsl, parses every .vl document, and emits a
// structured JSON index + a human-readable summary. Read-only, no vvvv runtime.
// (Indexing logic lives in Indexer.Build so the MCP server can reuse it.)

var project = GetArg("--project") ?? Directory.GetCurrentDirectory();
var outPath = GetArg("--out") ?? Path.Combine(project, "vl-map.json");
var quiet = args.Contains("--quiet");

project = Path.GetFullPath(project);
if (!Directory.Exists(project))
{
    Console.Error.WriteLine($"Project dir not found: {project}");
    return 1;
}

var index = Indexer.Build(project);

var json = JsonSerializer.Serialize(index, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});
File.WriteAllText(outPath, json);

if (!quiet)
    PrintSummary(index, outPath);

return 0;

// ---- helpers ----

void PrintSummary(ProjectIndex idx, string jsonPath)
{
    var docsWithWarnings = idx.Documents.Count(d => d.Warnings.Count > 0);

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

string? GetArg(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
