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

        return info;
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
