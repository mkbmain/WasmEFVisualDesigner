using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Archive;

/// Bridges "many original .cs files" and "one editable blob" so DiagramEditor's rewriters, which
/// operate on a single ClassSource/ConfigSource string, can round-trip a multi-file upload without
/// producing illegal C# (multiple `using` blocks or file-scoped `namespace` declarations
/// concatenated into one compilation unit — backlog item F3).
///
/// `Merge` folds N files into one syntactically valid compilation unit: usings are hoisted and
/// deduplicated, every file's own namespace (file-scoped or block-scoped) is preserved as its own
/// block so files that used different namespaces can still coexist as siblings, and bare top-level
/// statements from every file are kept together ahead of any namespace/type declarations (the same
/// ordering C# requires of a single file that mixes top-level statements with extra type
/// declarations).
public static class MultiFileSourceMerger
{
    public static string Merge(IReadOnlyList<string> fileContents)
    {
        if (fileContents.Count == 0)
        {
            return string.Empty;
        }

        if (fileContents.Count == 1)
        {
            return fileContents[0];
        }

        var mergedUsings = new List<UsingDirectiveSyntax>();
        var seenUsings = new HashSet<string>();
        var globalStatements = new List<MemberDeclarationSyntax>();
        var otherMembers = new List<MemberDeclarationSyntax>();

        foreach (var fileContent in fileContents)
        {
            var root = CSharpSyntaxTree.ParseText(fileContent).GetCompilationUnitRoot();

            foreach (var usingDirective in root.Usings)
            {
                if (seenUsings.Add(usingDirective.ToString().Trim()))
                {
                    mergedUsings.Add(usingDirective);
                }
            }

            foreach (var member in root.Members)
            {
                if (member is GlobalStatementSyntax globalStatement)
                {
                    globalStatements.Add(globalStatement);
                }
                else if (member is FileScopedNamespaceDeclarationSyntax fileScopedNamespace)
                {
                    // A compilation unit can only have one file-scoped namespace; converting to
                    // the equivalent block form lets N files' namespaces coexist as siblings.
                    otherMembers.Add(
                        SyntaxFactory.NamespaceDeclaration(fileScopedNamespace.Name)
                            .WithMembers(fileScopedNamespace.Members));
                }
                else
                {
                    otherMembers.Add(member);
                }
            }
        }

        var mergedRoot = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(mergedUsings))
            .WithMembers(SyntaxFactory.List(globalStatements.Concat(otherMembers)));

        return mergedRoot.NormalizeWhitespace().ToFullString();
    }

    /// Reverses `Merge` for the (possibly edited) merged source, routing each top-level
    /// declaration/statement back to its original file via `fileOrigins` (entity/config name ->
    /// path). Anything with no recorded origin (e.g. a brand-new entity added in the editor) falls
    /// back to `defaultPath`. A file that ends up with nothing routed to it is omitted.
    /// When a config file configures more than one entity via bare method-body calls (the
    /// `ResolveEntityNameForType` DbContext-shaped-class fallback), this assumes every entity
    /// configured inside that one type shares the same recorded origin in `fileOrigins` — if a
    /// caller violates that, entities are silently routed by whichever configured entity is found
    /// first, not an error.
    public static IReadOnlyDictionary<string, string> Split(
        string mergedSource,
        IReadOnlyDictionary<string, string> fileOrigins,
        string defaultPath)
    {
        var distinctPaths = fileOrigins.Values.Distinct().ToList();
        if (distinctPaths.Count <= 1)
        {
            var singlePath = distinctPaths.Count == 1 ? distinctPaths[0] : defaultPath;
            return new Dictionary<string, string> { [singlePath] = mergedSource };
        }

        var root = CSharpSyntaxTree.ParseText(mergedSource).GetCompilationUnitRoot();
        var routed = new List<(string Path, string? Namespace, MemberDeclarationSyntax Member)>();

        string ResolvePath(string? name) =>
            name is not null && fileOrigins.TryGetValue(name, out var path) ? path : defaultPath;

        string? ResolveEntityNameForType(TypeDeclarationSyntax type)
        {
            if (type is ClassDeclarationSyntax classDeclaration)
            {
                var configuredEntityName = FluentSyntaxHelpers.TryGetEntityTypeConfigurationEntityName(classDeclaration);
                if (configuredEntityName is not null)
                {
                    return configuredEntityName;
                }
            }

            if (fileOrigins.ContainsKey(type.Identifier.Text))
            {
                return type.Identifier.Text;
            }

            // A DbContext-shaped class configuring one or more entities via bare Entity<T>()
            // calls inside a method body: route the whole class alongside whichever entity it
            // configures (only reachable when every entity inside it shares one origin, since the
            // 2-distinct-path short-circuit above already handled the single-origin case).
            return type.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
                .FirstOrDefault(name => name is not null);
        }

        void Route(MemberDeclarationSyntax member, string? namespaceName)
        {
            switch (member)
            {
                case GlobalStatementSyntax globalStatement:
                    var entityName = globalStatement.DescendantNodesAndSelf()
                        .OfType<InvocationExpressionSyntax>()
                        .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
                        .FirstOrDefault(name => name is not null);
                    routed.Add((ResolvePath(entityName), null, member));
                    break;

                case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                    foreach (var nested in fileScopedNamespace.Members)
                    {
                        Route(nested, fileScopedNamespace.Name.ToString());
                    }

                    break;

                case NamespaceDeclarationSyntax namespaceDeclaration:
                    foreach (var nested in namespaceDeclaration.Members)
                    {
                        Route(nested, namespaceDeclaration.Name.ToString());
                    }

                    break;

                case TypeDeclarationSyntax typeDeclaration:
                    routed.Add((ResolvePath(ResolveEntityNameForType(typeDeclaration)), namespaceName, member));
                    break;

                default:
                    routed.Add((defaultPath, namespaceName, member));
                    break;
            }
        }

        foreach (var member in root.Members)
        {
            Route(member, namespaceName: null);
        }

        var pathOrder = new List<string>();
        var byPath = new Dictionary<string, List<(string? Namespace, MemberDeclarationSyntax Member)>>();
        foreach (var (path, ns, member) in routed)
        {
            if (!byPath.TryGetValue(path, out var members))
            {
                members = new List<(string? Namespace, MemberDeclarationSyntax Member)>();
                byPath[path] = members;
                pathOrder.Add(path);
            }

            members.Add((ns, member));
        }

        var result = new Dictionary<string, string>();
        foreach (var path in pathOrder)
        {
            result[path] = BuildFileContent(root.Usings, byPath[path]);
        }

        return result;
    }

    private static string BuildFileContent(
        SyntaxList<UsingDirectiveSyntax> usings,
        List<(string? Namespace, MemberDeclarationSyntax Member)> entries)
    {
        var globalStatements = entries.Where(e => e.Member is GlobalStatementSyntax).Select(e => e.Member).ToList();

        var namespaceOrder = new List<string?>();
        var byNamespace = new Dictionary<string, List<MemberDeclarationSyntax>>();
        foreach (var (ns, member) in entries)
        {
            if (member is GlobalStatementSyntax)
            {
                continue;
            }

            var key = ns ?? "";
            if (!byNamespace.TryGetValue(key, out var members))
            {
                members = new List<MemberDeclarationSyntax>();
                byNamespace[key] = members;
                namespaceOrder.Add(ns);
            }

            members.Add(member);
        }

        var finalMembers = new List<MemberDeclarationSyntax>(globalStatements);
        foreach (var ns in namespaceOrder)
        {
            var members = byNamespace[ns ?? ""];
            if (ns is null)
            {
                finalMembers.AddRange(members);
            }
            else
            {
                finalMembers.Add(
                    SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns))
                        .WithMembers(SyntaxFactory.List(members)));
            }
        }

        var newRoot = SyntaxFactory.CompilationUnit()
            .WithUsings(usings)
            .WithMembers(SyntaxFactory.List(finalMembers));

        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
