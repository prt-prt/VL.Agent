namespace VlMap;

/// <summary>Top-level index of a vvvv project.</summary>
public sealed class ProjectIndex
{
    public required string Root { get; init; }
    public required string GeneratedBy { get; init; }
    public FileCounts Files { get; init; } = new();
    public List<VlDocumentInfo> Documents { get; init; } = [];
    public List<PackageUse> Packages { get; init; } = [];
    public List<DocEdge> DocumentEdges { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class FileCounts
{
    public int Vl { get; set; }
    public int Cs { get; set; }
    public int Csproj { get; set; }
    public int Sdsl { get; set; }
}

public sealed class VlDocumentInfo
{
    public required string Path { get; init; }          // relative to root
    public string? Id { get; set; }
    public string? LanguageVersion { get; set; }
    public string? Version { get; set; }
    public List<Definition> Definitions { get; init; } = [];
    public List<NugetDep> Nuget { get; init; } = [];
    public List<DocDep> Documents { get; init; } = [];
    public List<string> Platform { get; init; } = [];
    public List<string> ReferencedLibraries { get; init; } = [];
    public ElementStats Stats { get; init; } = new();
    /// <summary>Resolved node/pad/link dataflow graph for this document (vertex-level).</summary>
    public PatchGraph? Graph { get; set; }
    public List<string> Warnings { get; init; } = [];
    public string? ParseError { get; set; }
}

/// <summary>
/// Vertex-level dataflow graph for a document: nodes and pads (IOBoxes) as
/// vertices, links resolved from pin/pad endpoints to their owning vertices.
/// Unowned route endpoints are kept as raw ids. Flattened across nested
/// patches; each element keeps its source pin ids.
/// </summary>
public sealed class PatchGraph
{
    public List<PatchNode> Nodes { get; init; } = [];
    public List<PatchPad> Pads { get; init; } = [];
    public List<PatchLink> Links { get; init; } = [];
}

public sealed class PatchNode
{
    public required string Id { get; init; }
    public string? Name { get; init; }        // node label (Name attr or NodeReference choice)
    public string? Category { get; init; }     // LastCategoryFullName of the NodeReference
    public string? Dependency { get; init; }   // LastDependency (library the node comes from)
    public string? Bounds { get; init; }
    public List<PatchPin> Pins { get; init; } = [];
}

public sealed class PatchPin
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Kind { get; init; }   // InputPin / OutputPin / StateInputPin / ...
}

public sealed class PatchPad
{
    public required string Id { get; init; }
    public string? Comment { get; init; }   // the IOBox label
    public string? Value { get; init; }
    public string? Type { get; init; }      // from the TypeAnnotation choice
    public string? Bounds { get; init; }
}

public sealed class PatchLink
{
    public required string Id { get; init; }
    public string? From { get; set; }       // owning-vertex id, or raw endpoint id if unresolved
    public string? To { get; set; }         // owning-vertex id, or raw endpoint id if unresolved
    public string? FromPin { get; init; }   // raw source endpoint id
    public string? ToPin { get; init; }     // raw target endpoint id
    public bool Hidden { get; init; }
}

public sealed class Definition
{
    public required string Name { get; init; }   // e.g. "PentagonMapper"
    public required string Kind { get; init; }   // e.g. "Process", "Class", "Record"
}

public sealed class NugetDep
{
    public required string Location { get; init; }
    public string? Version { get; init; }
}

public sealed class DocDep
{
    public required string Location { get; init; }   // as written, e.g. "./vl/Foo.vl"
    public string? Resolved { get; init; }           // relative-to-root, if found
    public bool Exists { get; init; }
}

public sealed class ElementStats
{
    public int Nodes { get; set; }
    public int Pads { get; set; }
    public int Links { get; set; }
    public int Canvases { get; set; }
    public int Pins { get; set; }
}

/// <summary>Project-wide view of a package and the versions it is pinned to.</summary>
public sealed class PackageUse
{
    public required string Location { get; init; }
    public required List<string> Versions { get; init; }
    public int DocumentCount { get; init; }
    public bool VersionConflict => Versions.Count > 1;
}

public sealed class DocEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public bool Resolved { get; init; }
}
