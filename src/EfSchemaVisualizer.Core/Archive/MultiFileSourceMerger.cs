using System.Collections.Generic;
using System.Linq;
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
}
