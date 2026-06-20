using System.Xml.Linq;

namespace VlMap;

/// <summary>
/// Static parser for a single .vl document. Works purely on the XML
/// (see the .vl file format) — no vvvv runtime involved. Queries by local
/// name throughout so the property/reflection namespaces never trip us up.
/// </summary>
public static class VlParser
{
    private static readonly HashSet<string> DefinitionKinds = new(StringComparer.Ordinal)
    {
        "ContainerDefinition", "ClassDefinition", "RecordDefinition",
        "InterfaceDefinition", "ForwardDefinition",
    };

    public static VlDocumentInfo Parse(string absPath, string root)
    {
        var info = new VlDocumentInfo { Path = Rel(root, absPath) };

        XDocument doc;
        try
        {
            doc = XDocument.Load(absPath);
        }
        catch (Exception ex)
        {
            info.ParseError = ex.Message;
            info.Warnings.Add($"failed to parse XML: {ex.Message}");
            return info;
        }

        var docEl = doc.Root;
        if (docEl is null || docEl.Name.LocalName != "Document")
        {
            info.ParseError = "root element is not <Document>";
            info.Warnings.Add(info.ParseError);
            return info;
        }

        info.Id = (string?)docEl.Attribute("Id");
        info.LanguageVersion = (string?)docEl.Attribute("LanguageVersion");
        info.Version = (string?)docEl.Attribute("Version");

        // Dependencies are direct children of <Document>.
        foreach (var dep in docEl.Elements())
        {
            switch (dep.Name.LocalName)
            {
                case "NugetDependency":
                    info.Nuget.Add(new NugetDep
                    {
                        Location = (string?)dep.Attribute("Location") ?? "?",
                        Version = (string?)dep.Attribute("Version"),
                    });
                    break;
                case "DocumentDependency":
                    var loc = (string?)dep.Attribute("Location") ?? "?";
                    var (resolved, exists) = ResolveDocDep(absPath, root, loc);
                    info.Documents.Add(new DocDep { Location = loc, Resolved = resolved, Exists = exists });
                    if (!exists)
                        info.Warnings.Add($"missing document dependency: {loc}");
                    break;
                case "PlatformDependency":
                    info.Platform.Add((string?)dep.Attribute("Location") ?? "?");
                    break;
            }
        }

        // Walk every descendant once: counts, definitions, IDs, referenced libs.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var libs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var el in docEl.DescendantsAndSelf())
        {
            var ln = el.Name.LocalName;
            switch (ln)
            {
                case "Node": info.Stats.Nodes++; break;
                case "Pad": info.Stats.Pads++; break;
                case "Link": info.Stats.Links++; break;
                case "Canvas": info.Stats.Canvases++; break;
                case "Pin": info.Stats.Pins++; break;
            }

            var id = (string?)el.Attribute("Id");
            if (id is not null)
                ids[id] = ids.GetValueOrDefault(id) + 1;

            // LastDependency on NodeReference / TypeReference / TypeAnnotation
            // tells us which libraries this document actually pulls nodes from.
            var lastDep = (string?)el.Attribute("LastDependency");
            if (!string.IsNullOrEmpty(lastDep))
                libs.Add(lastDep);

            // A definition is a Node carrying a NodeReference whose Choice is a *Definition.
            if (ln == "Node")
            {
                var def = TryReadDefinition(el);
                if (def is not null)
                    info.Definitions.Add(def);
            }
        }

        info.ReferencedLibraries.AddRange(libs.OrderBy(x => x, StringComparer.Ordinal));

        foreach (var (id, count) in ids)
            if (count > 1)
                info.Warnings.Add($"duplicate Id '{id}' ({count}×) — violates uniqueness rule");

        info.Graph = BuildGraph(docEl);

        return info;
    }

    /// <summary>
    /// Build the vertex-level dataflow graph: collect Nodes/Pads (vertices) and
    /// Links, mapping pin/pad endpoints to owning vertices. Direct node
    /// control-points fold into that node; free route points stay unresolved so
    /// the viewer can avoid inventing false node-to-node edges.
    /// </summary>
    private static PatchGraph BuildGraph(XElement docEl)
    {
        var g = new PatchGraph();
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(docEl, null, null, g, owner);

        foreach (var l in g.Links)
        {
            if (l.FromPin is not null) l.From = owner.GetValueOrDefault(l.FromPin, l.FromPin);
            if (l.ToPin is not null) l.To = owner.GetValueOrDefault(l.ToPin, l.ToPin);
        }
        return g;
    }

    private static void Collect(
        XElement el,
        string? directNodeId,
        string? patchPinOwnerId,
        PatchGraph g,
        Dictionary<string, string> owner)
    {
        foreach (var child in el.Elements())
        {
            var ln = child.Name.LocalName;
            var id = (string?)child.Attribute("Id");
            switch (ln)
            {
                case "Node" when id is not null:
                    var nodeRef = child.Elements().FirstOrDefault(e => e.Name.LocalName == "NodeReference");
                    var node = new PatchNode
                    {
                        Id = id,
                        Name = NodeLabel(child, nodeRef),
                        Category = (string?)nodeRef?.Attribute("LastCategoryFullName"),
                        Dependency = (string?)nodeRef?.Attribute("LastDependency"),
                        Bounds = (string?)child.Attribute("Bounds"),
                    };
                    foreach (var pin in child.Elements().Where(e => e.Name.LocalName == "Pin"))
                    {
                        var pid = (string?)pin.Attribute("Id");
                        if (pid is null) continue;
                        node.Pins.Add(new PatchPin
                        {
                            Id = pid,
                            Name = (string?)pin.Attribute("Name"),
                            Kind = (string?)pin.Attribute("Kind"),
                        });
                        owner[pid] = id;
                    }
                    g.Nodes.Add(node);
                    owner[id] = id;
                    foreach (var nodeChild in child.Elements())
                    {
                        var childOwner = nodeChild.Name.LocalName == "Patch" ? null : id;
                        var childPatchPinOwner = nodeChild.Name.LocalName == "Patch" ? id : patchPinOwnerId;
                        Collect(nodeChild, childOwner, childPatchPinOwner, g, owner);
                    }
                    break;

                case "Pad" when id is not null:
                    g.Pads.Add(new PatchPad
                    {
                        Id = id,
                        Comment = (string?)child.Attribute("Comment"),
                        Value = (string?)child.Attribute("Value"),
                        Type = PadType(child),
                        Bounds = (string?)child.Attribute("Bounds"),
                    });
                    owner[id] = id;
                    Collect(child, null, patchPinOwnerId, g, owner);
                    break;

                case "Pin" when id is not null:
                    if (directNodeId is not null && child.Parent?.Name.LocalName == "Node")
                        owner[id] = directNodeId;
                    else if (patchPinOwnerId is not null && child.Parent?.Name.LocalName == "Patch")
                        owner[id] = patchPinOwnerId;
                    Collect(child, null, patchPinOwnerId, g, owner);
                    break;

                case "ControlPoint" when id is not null:
                    if (directNodeId is not null && child.Parent?.Name.LocalName == "Node")
                        owner[id] = directNodeId;
                    Collect(child, null, patchPinOwnerId, g, owner);
                    break;

                case "Link" when id is not null:
                    var endpoints = ((string?)child.Attribute("Ids") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (endpoints.Length == 2)
                        g.Links.Add(new PatchLink
                        {
                            Id = id,
                            FromPin = endpoints[0].Trim(),
                            ToPin = endpoints[1].Trim(),
                            Hidden = (string?)child.Attribute("IsHidden") == "true",
                        });
                    Collect(child, null, patchPinOwnerId, g, owner);
                    break;

                default:
                    Collect(child, null, patchPinOwnerId, g, owner);
                    break;
            }
        }
    }

    private static string? NodeLabel(XElement node, XElement? nodeRef)
    {
        var name = (string?)node.Attribute("Name");
        if (!string.IsNullOrWhiteSpace(name)) return name;
        // Fall back to the most specific NodeReference choice (skip the generic "Node" flag).
        var choice = nodeRef?.Elements()
            .Where(e => e.Name.LocalName == "Choice")
            .LastOrDefault(e => (string?)e.Attribute("Name") is { } n && n != "Node");
        return (string?)choice?.Attribute("Name");
    }

    private static string? PadType(XElement pad)
    {
        var ann = pad.Elements().FirstOrDefault(e => e.Name.LocalName == "TypeAnnotation");
        var choice = ann?.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
        return (string?)choice?.Attribute("Name");
    }

    private static Definition? TryReadDefinition(XElement node)
    {
        var nodeRef = node.Elements().FirstOrDefault(e => e.Name.LocalName == "NodeReference");
        if (nodeRef is null) return null;

        var choice = nodeRef.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Choice"
                && DefinitionKinds.Contains((string?)e.Attribute("Kind") ?? ""));
        if (choice is null) return null;

        var name = (string?)node.Attribute("Name");
        if (string.IsNullOrWhiteSpace(name)) return null;   // anonymous / non-definition

        return new Definition
        {
            Name = name,
            Kind = (string?)choice.Attribute("Name") ?? (string?)choice.Attribute("Kind") ?? "?",
        };
    }

    private static (string? resolved, bool exists) ResolveDocDep(string fromFile, string root, string location)
    {
        // Only relative paths are resolvable to local files; package-internal
        // references (no separators / not starting with .) are left unresolved.
        var isRelative = location.StartsWith('.') || location.Contains('/') || location.Contains('\\');
        if (!isRelative) return (null, false);

        try
        {
            var baseDir = System.IO.Path.GetDirectoryName(fromFile)!;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, location));
            return (Rel(root, full), File.Exists(full));
        }
        catch
        {
            return (null, false);
        }
    }

    private static string Rel(string root, string path)
    {
        var r = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
        return r;
    }
}
