using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class FluentConfigParser
{
    public IReadOnlyList<MaxLengthConfig> ParseMaxLengths(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<MaxLengthConfig>();

        // Distinct entity names configured anywhere in the file.
        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var maxLengthCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall);
                    var arg = maxLengthCall.ArgumentList.Arguments.FirstOrDefault();

                    if (propertyName is null || arg is null)
                    {
                        continue;
                    }

                    if (int.TryParse(arg.Expression.ToString(), out var maxLength))
                    {
                        results.Add(new MaxLengthConfig(entityName!, propertyName, maxLength));
                    }
                }
            }
        }

        return results;
    }
}
