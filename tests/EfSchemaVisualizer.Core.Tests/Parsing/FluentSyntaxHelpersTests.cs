using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentSyntaxHelpersTests
{
    private static InvocationExpressionSyntax ParseSingleInvocation(string callExpression)
    {
        var wrapped = $$"""
            public class C
            {
                void M()
                {
                    {{callExpression}};
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>().Last();
    }

    [Fact]
    public void TryReadPropertyNameList_SingleLambda_ReturnsSingleName()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => e.Id)");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "Id" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_CompositeLambda_ReturnsNamesInOrder()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => new { e.A, e.B })");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_BareStringParams_ReturnsNamesInOrder()
    {
        var invocation = ParseSingleInvocation("""entity.HasKey("A", "B")""");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_ExplicitNameAnonymousMember_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => new { K = e.A })");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Null(names);
    }

    [Fact]
    public void TryReadPropertyNameList_NoArguments_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasKey()");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Null(names);
    }
}
