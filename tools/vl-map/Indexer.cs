namespace VlMap;

/// <summary>
/// Builds a <see cref="ProjectIndex"/> for a vvvv project directory. Pure and
/// reusable — used by the vl-map CLI and the MCP server alike.
/// </summary>
public static class Indexer
{
    public static ProjectIndex Build(string project)
    {
        project = Path.GetFullPath(project);
        var index = new ProjectIndex { Root = project, GeneratedBy = "vl-map" };

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

        var parseFailures = index.Documents.Count(d => d.ParseError is not null);
        if (parseFailures > 0)
            index.Warnings.Add($"{parseFailures} document(s) failed to parse");

        return index;
    }

    private static bool Skip(string dir)
    {
        var n = Path.GetFileName(dir);
        return n is ".git" or "bin" or "obj" or ".vs";
    }

    private static IEnumerable<string> EnumerateFiles(string root)
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
}
