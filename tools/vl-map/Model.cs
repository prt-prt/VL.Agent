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
    public List<string> Warnings { get; init; } = [];
    public string? ParseError { get; set; }
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
