# Data-annotation parsing & P0 trust diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three Round-2 Priority-0 backlog items — data-annotation attributes are silently unread, duplicate entity names collide silently, and nested type declarations vanish without a diagnostic — by extending `EntityClassParser` to read `[Key]`, `[Required]`, `[MaxLength]`/`[StringLength]`, `[Column]`, `[Table]`, `[Precision]`, and `[ForeignKey]` into the existing model, and by adding two new structural diagnostics.

**Architecture:** All changes live in `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, plus a small integration change in `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs` to union annotation-derived relationships with fluent ones. Scalar annotations ([Key]/[Required]/[MaxLength]/[StringLength]/[Column]/[Table]/[Precision]) populate `EntityModel`/`PropertyModel` fields directly during the existing CLR-shape parse pass — no new `Merging` DTOs, because `ModelMerger.Apply*` already only overwrites a field when a matching fluent config exists, so fluent-wins precedence falls out for free. `[ForeignKey]` gets its own `EntityClassParser.ParseRelationships` method (mirroring `FluentConfigParser.ParseRelationships`'s signature) since relationships require cross-referencing all parsed entities, not just one property.

**Tech Stack:** C#, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Non-literal annotation arguments (e.g. `[MaxLength(MaxNameLength)]`) are skipped silently, matching the existing accepted limitation for fluent config (`FluentConfigParser`'s `int.TryParse` pattern) — no new diagnostic for this sub-case.
- Fluent API always wins over data annotations on conflict — enforced by relying on `ModelMerger.Apply*`'s existing "only overwrite if a fluent config exists" behavior; `ModelMerger` itself is not modified.
- Attribute matching is by simple name text only (`"Key"` or `"KeyAttribute"`), the same pattern `EntityClassParser.HasNotMappedAttribute` already uses — no semantic/namespace resolution, consistent with this parser's syntax-only design.
- `[DatabaseGenerated]`, `[InverseProperty]`, and annotations on record positional parameters are out of scope for this plan (see the design doc's "Out of scope" section).
- Every new/changed method must have `EntityClassParserTests` (or, for Task 5, `DiagramModelBuilderTests`) coverage before being considered done — this project's existing tests are all TDD-style, one behavior per test.

---

### Task 1: Duplicate entity name and nested type diagnostics

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs:12-40` (the `Parse` method)
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `DiagnosticCodes.DuplicateEntityName`, `DiagnosticCodes.NestedTypeDeclaration` — used by later tasks' tests indirectly (none) and by the UI in a future pass (not this plan).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_DuplicateEntityNames_EmitsDiagnostic_AndKeepsFirst()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Person
            {
                public int Id { get; set; }
                public string? Nickname { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = Assert.Single(result.Value);
        Assert.Equal("Person", entity.Name);
        Assert.Single(entity.Properties); // the first declaration's shape, not the second's

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateEntityName, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
    }

    [Fact]
    public void Parse_NestedTypeDeclaration_EmitsDiagnostic_AndIsExcluded()
    {
        const string source = """
            public class Outer
            {
                public int Id { get; set; }

                public class Inner
                {
                    public int Id { get; set; }
                }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = Assert.Single(result.Value);
        Assert.Equal("Outer", entity.Name);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.NestedTypeDeclaration, diagnostic.Code);
        Assert.Equal("Inner", diagnostic.EntityName);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~Parse_DuplicateEntityNames_EmitsDiagnostic_AndKeepsFirst|FullyQualifiedName~Parse_NestedTypeDeclaration_EmitsDiagnostic_AndIsExcluded"`

Expected: both FAIL — `Parse_DuplicateEntityNames...` fails because `result.Value` currently contains 2 entities and `result.Diagnostics` is empty; `Parse_NestedTypeDeclaration...` fails because `DiagnosticCodes.NestedTypeDeclaration` doesn't exist yet (compile error), or once stubbed, because `result.Diagnostics` is empty.

- [ ] **Step 3: Add the two new diagnostic codes**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add two constants alongside the existing ones:

```csharp
    public const string DuplicateEntityName = nameof(DuplicateEntityName);
    public const string NestedTypeDeclaration = nameof(NestedTypeDeclaration);
```

- [ ] **Step 4: Rewrite `EntityClassParser.Parse`**

Replace the full `Parse` method (`EntityClassParser.cs:12-40`) with:

```csharp
    public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var allTypeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .ToList();

        var typeDeclarations = allTypeDeclarations
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .ToList();

        var diagnostics = new List<Diagnostic>();

        foreach (var nested in allTypeDeclarations.Except(typeDeclarations))
        {
            var enclosing = nested.Ancestors().OfType<TypeDeclarationSyntax>().First();
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.NestedTypeDeclaration,
                $"'{nested.Identifier.Text}' is nested inside '{enclosing.Identifier.Text}' and was skipped; nested type declarations are not parsed as entities.",
                nested.Identifier.Text,
                PropertyName: null,
                nested.Span));
        }

        if (typeDeclarations.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.NoEntityDeclarations,
                "No class, record, or struct declarations found in file; nothing to parse.",
                EntityName: null,
                PropertyName: null,
                root.Span));

            return new ParseResult<IReadOnlyList<EntityModel>>(new List<EntityModel>(), diagnostics);
        }

        var entities = typeDeclarations.Select(ParseEntity).ToList();

        foreach (var duplicateGroup in entities.GroupBy(e => e.Name).Where(g => g.Count() > 1))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.DuplicateEntityName,
                $"{duplicateGroup.Count()} entity declarations share the name '{duplicateGroup.Key}'; only the first is used.",
                duplicateGroup.Key,
                PropertyName: null,
                root.Span));
        }

        var deduplicatedEntities = entities
            .GroupBy(e => e.Name)
            .Select(g => g.First())
            .ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(deduplicatedEntities, diagnostics);
    }
```

- [ ] **Step 5: Run the full `EntityClassParserTests` suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: PASS — all existing tests plus the two new ones, including the pre-existing `Parse_ClassLessFile_ReturnsEmptyListAndDiagnostic_NoException` test (still exactly one diagnostic for an enum-only file, since there are no nested types in that fixture).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Add duplicate-entity-name and nested-type-declaration diagnostics"
```

---

### Task 2: Property-level data annotations (`[Required]`, `[MaxLength]`/`[StringLength]`, `[Column]`, `[Precision]`)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs` (`ParseProperty`, plus new private helpers)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `PropertyModel` (existing, unchanged shape: `Name, ClrType, IsNullable, MaxLength, IsRequiredOverride, Precision, Scale, ColumnName, ColumnType, DefaultValueLiteral`).
- Produces: private helpers `FindAttribute(SyntaxList<AttributeListSyntax>, string)`, `GetPositionalArg(AttributeSyntax, int)`, `GetNamedArg(AttributeSyntax, string)`, `TryReadStringArg(AttributeArgumentSyntax?)`, `TryReadIntArg(AttributeArgumentSyntax?)` — reused by Task 3 (entity-level annotations) and Task 4 (`[ForeignKey]`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_RequiredAttribute_SetsIsRequiredOverride()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Required]
                public string? Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.True(name.IsRequiredOverride);
    }

    [Fact]
    public void Parse_MaxLengthAttribute_SetsMaxLength()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [MaxLength(50)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(50, name.MaxLength);
    }

    [Fact]
    public void Parse_StringLengthAttribute_SetsMaxLength()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [StringLength(80)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(80, name.MaxLength);
    }

    [Fact]
    public void Parse_MaxLengthAndStringLengthBothPresent_MaxLengthWins()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [MaxLength(50)]
                [StringLength(80)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(50, name.MaxLength);
    }

    [Fact]
    public void Parse_MaxLengthWithNonLiteralArgument_SkipsSilently_NoException()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [MaxLength(MaxNameLength)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Null(name.MaxLength);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parse_ColumnAttribute_SetsColumnNameAndType()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Column(Name = "full_name", TypeName = "varchar(80)")]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal("full_name", name.ColumnName);
        Assert.Equal("varchar(80)", name.ColumnType);
    }

    [Fact]
    public void Parse_ColumnAttributePositionalName_SetsColumnName()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Column("full_name")]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal("full_name", name.ColumnName);
    }

    [Fact]
    public void Parse_PrecisionAttribute_SetsPrecisionAndScale()
    {
        const string source = """
            public class Invoice
            {
                public int Id { get; set; }
                [Precision(18, 2)]
                public decimal Total { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var total = result.Value.Single().Properties.Single(p => p.Name == "Total");
        Assert.Equal(18, total.Precision);
        Assert.Equal(2, total.Scale);
    }

    [Fact]
    public void Parse_PrecisionAttributeNoScale_SetsPrecisionOnly()
    {
        const string source = """
            public class Invoice
            {
                public int Id { get; set; }
                [Precision(18)]
                public decimal Total { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var total = result.Value.Single().Properties.Single(p => p.Name == "Total");
        Assert.Equal(18, total.Precision);
        Assert.Null(total.Scale);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: the 9 new tests FAIL (all annotation fields currently read as `null`); all pre-existing tests still PASS.

- [ ] **Step 3: Add the attribute-reading helpers and rewrite `ParseProperty`**

In `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, replace the existing `ParseProperty` method with:

```csharp
    private static PropertyModel ParseProperty(PropertyDeclarationSyntax property)
    {
        var isNullable = property.Type is NullableTypeSyntax;
        var clrType = property.Type is NullableTypeSyntax nullableType
            ? nullableType.ElementType.ToString()
            : property.Type.ToString();

        var attributeLists = property.AttributeLists;

        bool? isRequiredOverride = FindAttribute(attributeLists, "Required") is not null ? true : null;

        int? maxLength = null;
        if (FindAttribute(attributeLists, "MaxLength") is { } maxLengthAttr)
        {
            maxLength = TryReadIntArg(GetPositionalArg(maxLengthAttr, 0));
        }
        else if (FindAttribute(attributeLists, "StringLength") is { } stringLengthAttr)
        {
            maxLength = TryReadIntArg(GetPositionalArg(stringLengthAttr, 0));
        }

        string? columnName = null;
        string? columnType = null;
        if (FindAttribute(attributeLists, "Column") is { } columnAttr)
        {
            columnName = TryReadStringArg(GetPositionalArg(columnAttr, 0))
                ?? TryReadStringArg(GetNamedArg(columnAttr, "Name"));
            columnType = TryReadStringArg(GetNamedArg(columnAttr, "TypeName"));
        }

        int? precision = null;
        int? scale = null;
        if (FindAttribute(attributeLists, "Precision") is { } precisionAttr)
        {
            precision = TryReadIntArg(GetPositionalArg(precisionAttr, 0));
            scale = TryReadIntArg(GetPositionalArg(precisionAttr, 1));
        }

        return new PropertyModel(
            property.Identifier.Text,
            clrType,
            isNullable,
            maxLength,
            isRequiredOverride,
            precision,
            scale,
            columnName,
            columnType);
    }
```

Then add these private helpers below `HasNotMappedAttribute`, and rewrite `HasNotMappedAttribute` itself to reuse `FindAttribute` (removes the now-duplicated string matching):

```csharp
    private static bool HasNotMappedAttribute(SyntaxList<AttributeListSyntax> attributeLists)
    {
        return FindAttribute(attributeLists, "NotMapped") is not null;
    }

    private static AttributeSyntax? FindAttribute(SyntaxList<AttributeListSyntax> attributeLists, string simpleName)
    {
        return attributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attribute => attribute.Name.ToString() is var name
                && (name == simpleName || name == simpleName + "Attribute"));
    }

    private static AttributeArgumentSyntax? GetPositionalArg(AttributeSyntax attribute, int index)
    {
        var positional = attribute.ArgumentList?.Arguments
            .Where(a => a.NameEquals is null)
            .ToList();

        return positional is not null && index < positional.Count ? positional[index] : null;
    }

    private static AttributeArgumentSyntax? GetNamedArg(AttributeSyntax attribute, string name)
    {
        return attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);
    }

    private static string? TryReadStringArg(AttributeArgumentSyntax? arg)
    {
        return arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;
    }

    private static int? TryReadIntArg(AttributeArgumentSyntax? arg)
    {
        return arg is not null && int.TryParse(arg.Expression.ToString(), out var value) ? value : null;
    }
```

- [ ] **Step 4: Run the full `EntityClassParserTests` suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: PASS — all tests including the 9 new ones and the pre-existing `NotMapped` coverage (unaffected by the `HasNotMappedAttribute` rewrite).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Parse Required/MaxLength/StringLength/Column/Precision annotations into PropertyModel"
```

---

### Task 3: Entity-level data annotations (`[Table]`, `[Key]` incl. composite ordering)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs` (`ParseEntity`, plus new private helpers)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `FindAttribute`, `GetPositionalArg`, `GetNamedArg`, `TryReadStringArg`, `TryReadIntArg` (from Task 2).
- Produces: `EntityModel.TableName`/`Schema`/`KeyPropertyNames` now populated from annotations when no fluent config overrides them (existing fields, no shape change).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_TableAttribute_SetsTableNameAndSchema()
    {
        const string source = """
            [Table("people", Schema = "dbo")]
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("people", entity.TableName);
        Assert.Equal("dbo", entity.Schema);
    }

    [Fact]
    public void Parse_KeyAttribute_SetsSinglePropertyKey()
    {
        const string source = """
            public class Person
            {
                [Key]
                public int PersonId { get; set; }
                public string? Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "PersonId" }, entity.KeyPropertyNames);
    }

    [Fact]
    public void Parse_CompositeKeyAttributes_OrderedByColumnOrder()
    {
        const string source = """
            public class OrderLine
            {
                [Key]
                [Column(Order = 2)]
                public int ProductId { get; set; }

                [Key]
                [Column(Order = 1)]
                public int OrderId { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "OrderId", "ProductId" }, entity.KeyPropertyNames);
    }

    [Fact]
    public void Parse_CompositeKeyAttributesNoOrder_UsesDeclarationOrder()
    {
        const string source = """
            public class OrderLine
            {
                [Key]
                public int OrderId { get; set; }

                [Key]
                public int ProductId { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "OrderId", "ProductId" }, entity.KeyPropertyNames);
    }

    [Fact]
    public void Parse_NoKeyAttribute_KeyPropertyNamesEmpty()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Value.Single().KeyPropertyNames);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: the 4 new key/table tests FAIL (`TableName`/`Schema`/`KeyPropertyNames` are currently always null/empty regardless of annotations); `Parse_NoKeyAttribute_KeyPropertyNamesEmpty` already PASSes today (no regression risk, included for explicitness before the behavior changes).

- [ ] **Step 3: Rewrite `ParseEntity` and add helpers**

Replace the existing `ParseEntity` method with:

```csharp
    private static EntityModel ParseEntity(TypeDeclarationSyntax typeDeclaration)
    {
        var positionalProperties = typeDeclaration is RecordDeclarationSyntax
            ? typeDeclaration.ParameterList?.Parameters.Select(ParseParameterProperty) ?? Enumerable.Empty<PropertyModel>()
            : Enumerable.Empty<PropertyModel>();

        var mappedProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(IsMappedInstanceProperty)
            .ToList();

        var bodyProperties = mappedProperties.Select(ParseProperty);

        var properties = positionalProperties.Concat(bodyProperties).ToList();

        var keyPropertyNames = ResolveKeyPropertyNames(mappedProperties);
        var (tableName, schema) = ParseTableAttribute(typeDeclaration.AttributeLists);

        return new EntityModel(
            typeDeclaration.Identifier.Text,
            properties,
            keyPropertyNames,
            TableName: tableName,
            Schema: schema);
    }

    private static IReadOnlyList<string> ResolveKeyPropertyNames(List<PropertyDeclarationSyntax> mappedProperties)
    {
        var keyedProperties = mappedProperties
            .Where(p => FindAttribute(p.AttributeLists, "Key") is not null)
            .ToList();

        if (keyedProperties.Count == 0)
        {
            return new List<string>();
        }

        return keyedProperties
            .Select((p, index) => (Name: p.Identifier.Text, Order: GetColumnOrder(p), DeclarationIndex: index))
            .OrderBy(k => k.Order ?? int.MaxValue)
            .ThenBy(k => k.DeclarationIndex)
            .Select(k => k.Name)
            .ToList();
    }

    private static int? GetColumnOrder(PropertyDeclarationSyntax property)
    {
        return FindAttribute(property.AttributeLists, "Column") is { } columnAttr
            ? TryReadIntArg(GetNamedArg(columnAttr, "Order"))
            : null;
    }

    private static (string? TableName, string? Schema) ParseTableAttribute(SyntaxList<AttributeListSyntax> attributeLists)
    {
        if (FindAttribute(attributeLists, "Table") is not { } tableAttr)
        {
            return (null, null);
        }

        var tableName = TryReadStringArg(GetPositionalArg(tableAttr, 0));
        var schema = TryReadStringArg(GetNamedArg(tableAttr, "Schema"));
        return (tableName, schema);
    }
```

- [ ] **Step 4: Run the full `EntityClassParserTests` suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: PASS — all tests including the 5 new ones.

- [ ] **Step 5: Run the whole Core test project**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`

Expected: PASS, no regressions in `ModelMergerTests`, `FluentConfigParserTests`, `OnModelCreatingRewriterTests`, `EntityClassRewriterTests`, etc. — `ParseEntity`'s new `EntityModel(...)` constructor call still passes `Indexes` as its default (`null` → empty list via the record's `init`), unchanged from before.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Parse Table and Key annotations into EntityModel, including composite key ordering"
```

---

### Task 4: `[ForeignKey]` relationship resolution

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs` (new `ParseRelationships` method + private helpers)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `FindAttribute`, `GetPositionalArg`, `TryReadStringArg` (Task 2); `FluentSyntaxHelpers.TryGetElementTypeName(string clrType)` (existing, `internal`, same assembly); `RelationshipConfig` (`EfSchemaVisualizer.Core.Merging`, existing); `RelationshipKind` (`EfSchemaVisualizer.Core.Model`, existing).
- Produces: `public ParseResult<IReadOnlyList<RelationshipConfig>> EntityClassParser.ParseRelationships(string sourceCode, IReadOnlyList<EntityModel> entities)` — consumed by Task 5's `DiagramModelBuilder` wiring.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void ParseRelationships_ForeignKeyOnNavigationProperty_ResolvesOneToMany()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal("Blog", relationship.PrincipalEntity);
        Assert.Equal("Post", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Null(relationship.PrincipalNavigation);
        Assert.Equal("Blog", relationship.DependentNavigation);
        Assert.Equal(new[] { "BlogId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_ForeignKeyOnScalarProperty_ResolvesSameAsNavigationPlacement()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }

                [ForeignKey("Blog")]
                public int BlogId { get; set; }

                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal("Blog", relationship.PrincipalEntity);
        Assert.Equal("Post", relationship.DependentEntity);
        Assert.Equal(new[] { "BlogId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_PrincipalHasCollectionBackReference_ResolvesOneToManyWithPrincipalNavigation()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
                public ICollection<Post> Posts { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Posts", relationship.PrincipalNavigation);
    }

    [Fact]
    public void ParseRelationships_PrincipalHasScalarBackReference_ResolvesOneToOne()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
                public Post FeaturedPost { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("FeaturedPost", relationship.PrincipalNavigation);
    }

    [Fact]
    public void ParseRelationships_ForeignKeyNamesNonexistentProperty_SkipsSilently_NoException()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }

                [ForeignKey("DoesNotExist")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        Assert.Empty(relationshipResult.Value);
        Assert.Empty(relationshipResult.Diagnostics);
    }

    [Fact]
    public void ParseRelationships_NeitherSideIsKnownEntity_SkipsSilently()
    {
        const string source = """
            public class Post
            {
                public int Id { get; set; }
                public int PublisherId { get; set; }

                [ForeignKey("PublisherId")]
                public string Publisher { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        Assert.Empty(relationshipResult.Value);
    }

    [Fact]
    public void ParseRelationships_BothSidesAnnotated_ProducesOneRelationshipNotTwo()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }

                [ForeignKey("Blog")]
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        Assert.Single(relationshipResult.Value);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: compile error (`ParseRelationships` doesn't exist yet) — confirms the tests are wired to the not-yet-built method.

- [ ] **Step 3: Add `ParseRelationships` and its private helpers**

Add `using EfSchemaVisualizer.Core.Merging;` to the top of `EntityClassParser.cs` (alongside the existing `using EfSchemaVisualizer.Core.Model;`). Then add:

```csharp
    public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
        string sourceCode, IReadOnlyList<EntityModel> entities)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<RelationshipConfig>();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        foreach (var typeDeclaration in typeDeclarations)
        {
            var dependentEntityName = typeDeclaration.Identifier.Text;

            foreach (var property in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (FindAttribute(property.AttributeLists, "ForeignKey") is not { } foreignKeyAttr)
                {
                    continue;
                }

                var pairedPropertyName = TryReadStringArg(GetPositionalArg(foreignKeyAttr, 0));
                if (pairedPropertyName is null)
                {
                    continue;
                }

                var pairedProperty = typeDeclaration.Members
                    .OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(p => p.Identifier.Text == pairedPropertyName);

                if (pairedProperty is null)
                {
                    continue;
                }

                var relationship = TryResolveForeignKeyRelationship(dependentEntityName, property, pairedProperty, entities);
                if (relationship is not null)
                {
                    results.Add(relationship);
                }
            }
        }

        var deduplicated = results
            .GroupBy(r => (r.PrincipalEntity, r.DependentEntity, Fk: string.Join(",", r.ForeignKeyProperties)))
            .Select(g => g.First())
            .ToList();

        return new ParseResult<IReadOnlyList<RelationshipConfig>>(deduplicated, new List<Diagnostic>());
    }

    private static RelationshipConfig? TryResolveForeignKeyRelationship(
        string dependentEntityName,
        PropertyDeclarationSyntax annotatedProperty,
        PropertyDeclarationSyntax pairedProperty,
        IReadOnlyList<EntityModel> entities)
    {
        PropertyDeclarationSyntax navigationProperty;
        PropertyDeclarationSyntax fkProperty;

        if (TryGetNavigationTargetEntity(annotatedProperty, entities) is not null)
        {
            navigationProperty = annotatedProperty;
            fkProperty = pairedProperty;
        }
        else if (TryGetNavigationTargetEntity(pairedProperty, entities) is not null)
        {
            navigationProperty = pairedProperty;
            fkProperty = annotatedProperty;
        }
        else
        {
            return null;
        }

        var principalEntityName = TryGetNavigationTargetEntity(navigationProperty, entities)!;
        var principalEntity = entities.FirstOrDefault(e => e.Name == principalEntityName);
        if (principalEntity is null)
        {
            return null;
        }

        var (kind, principalNavigation) = FindPrincipalBackReference(principalEntity, dependentEntityName);

        return new RelationshipConfig(
            principalEntityName,
            dependentEntityName,
            kind,
            principalNavigation,
            navigationProperty.Identifier.Text,
            new List<string> { fkProperty.Identifier.Text });
    }

    private static string? TryGetNavigationTargetEntity(PropertyDeclarationSyntax property, IReadOnlyList<EntityModel> entities)
    {
        var typeText = property.Type is NullableTypeSyntax nullableType
            ? nullableType.ElementType.ToString()
            : property.Type.ToString();

        return entities.Any(e => e.Name == typeText) ? typeText : null;
    }

    private static (RelationshipKind Kind, string? PrincipalNavigation) FindPrincipalBackReference(
        EntityModel principalEntity, string dependentEntityName)
    {
        foreach (var property in principalEntity.Properties)
        {
            var elementTypeName = FluentSyntaxHelpers.TryGetElementTypeName(property.ClrType);

            if (elementTypeName != dependentEntityName)
            {
                continue;
            }

            var isCollection = elementTypeName != property.ClrType;
            return isCollection
                ? (RelationshipKind.OneToMany, property.Name)
                : (RelationshipKind.OneToOne, property.Name);
        }

        return (RelationshipKind.OneToMany, null);
    }
```

- [ ] **Step 4: Run the full `EntityClassParserTests` suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityClassParserTests"`

Expected: PASS — all 7 new relationship tests plus every pre-existing test.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Resolve ForeignKey annotations into relationships via EntityClassParser.ParseRelationships"
```

---

### Task 5: Wire annotation relationships into `DiagramModelBuilder`, with fluent-wins precedence tests

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test (new file): `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `EntityClassParser.ParseRelationships` (Task 4); `FluentConfigParser.ParseRelationships` (existing); `ModelMerger.ApplyRelationships` (existing, unchanged).
- Produces: `DiagramModelBuilder.Build`'s existing public signature and `DiagramModelResult` shape are unchanged — only its internal relationship-gathering logic changes, and it now also parses scalar annotations transparently (since `EntityClassParser.Parse`, called by `Build` already, is what changed in Tasks 2–3).

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`:

```csharp
using System.Linq;
using EfSchemaVisualizer.Web;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderTests
{
    [Fact]
    public void Build_FluentMaxLengthAndAnnotationMaxLengthOnSameProperty_FluentWins()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }

                [MaxLength(50)]
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var name = result.Entities.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(100, name.MaxLength);
    }

    [Fact]
    public void Build_AnnotationOnlyMaxLength_NoFluentConfig_AnnotationValueUsed()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }

                [MaxLength(50)]
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var name = result.Entities.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(50, name.MaxLength);
    }

    [Fact]
    public void Build_FluentRelationshipAndAnnotationForeignKeyForSamePair_FluentWins()
    {
        const string classSource = """
            public class Blog
            {
                public int Id { get; set; }
                public ICollection<Post> Posts { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasOne(p => p.Blog)
                            .WithMany(b => b.Posts)
                            .HasForeignKey(p => p.BlogId)
                            .OnDelete(DeleteBehavior.Cascade);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior); // only the fluent parse reads OnDelete; proves fluent's config survived, not the annotation's
    }

    [Fact]
    public void Build_AnnotationOnlyForeignKey_NoFluentConfig_RelationshipStillProduced()
    {
        const string classSource = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Blog", relationship.PrincipalEntity);
        Assert.Equal("Post", relationship.DependentEntity);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~DiagramModelBuilderTests"`

Expected: `Build_FluentMaxLengthAndAnnotationMaxLengthOnSameProperty_FluentWins` and `Build_AnnotationOnlyMaxLength_NoFluentConfig_AnnotationValueUsed` PASS already (Tasks 2/3 already made this work — no `DiagramModelBuilder` change needed for scalar fields, confirming the "free precedence" design claim). `Build_FluentRelationshipAndAnnotationForeignKeyForSamePair_FluentWins` and `Build_AnnotationOnlyForeignKey_NoFluentConfig_RelationshipStillProduced` FAIL — `result.Relationships` is empty in the annotation-only case, and in the both-present case nothing merges the annotation path in yet.

- [ ] **Step 3: Wire `EntityClassParser.ParseRelationships` into `Build`**

Replace the body of `DiagramModelBuilder.Build` with:

```csharp
    public static DiagramModelResult Build(string classSource, string configSource)
    {
        var entityParser = new EntityClassParser();
        var configParser = new FluentConfigParser();

        var entityResult = entityParser.Parse(classSource);
        var diagnostics = new List<Diagnostic>(entityResult.Diagnostics);

        var maxLengths = configParser.ParseMaxLengths(configSource);
        var precisions = configParser.ParsePrecisions(configSource);
        var isRequired = configParser.ParseIsRequired(configSource);
        var keys = configParser.ParseKeys(configSource);
        var tables = configParser.ParseTableMappings(configSource);
        var columnNames = configParser.ParseColumnNames(configSource);
        var columnTypes = configParser.ParseColumnTypes(configSource);
        var defaultValues = configParser.ParseDefaultValues(configSource);
        var indexes = configParser.ParseIndexes(configSource);
        var fluentRelationships = configParser.ParseRelationships(configSource, entityResult.Value);
        var annotationRelationships = entityParser.ParseRelationships(classSource, entityResult.Value);

        diagnostics.AddRange(maxLengths.Diagnostics);
        diagnostics.AddRange(precisions.Diagnostics);
        diagnostics.AddRange(isRequired.Diagnostics);
        diagnostics.AddRange(keys.Diagnostics);
        diagnostics.AddRange(tables.Diagnostics);
        diagnostics.AddRange(columnNames.Diagnostics);
        diagnostics.AddRange(columnTypes.Diagnostics);
        diagnostics.AddRange(defaultValues.Diagnostics);
        diagnostics.AddRange(indexes.Diagnostics);
        diagnostics.AddRange(fluentRelationships.Diagnostics);
        diagnostics.AddRange(annotationRelationships.Diagnostics);

        var entities = entityResult.Value
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, indexes.Value))
            .ToList();

        var fluentRelationshipKeys = fluentRelationships.Value
            .Select(RelationshipDedupeKey)
            .ToHashSet();

        var mergedRelationshipConfigs = fluentRelationships.Value
            .Concat(annotationRelationships.Value.Where(r => !fluentRelationshipKeys.Contains(RelationshipDedupeKey(r))))
            .ToList();

        var relationshipModels = ModelMerger.ApplyRelationships(mergedRelationshipConfigs);

        return new DiagramModelResult(entities, relationshipModels, diagnostics);
    }

    private static (string PrincipalEntity, string DependentEntity, string ForeignKeyProperties) RelationshipDedupeKey(
        RelationshipConfig config)
    {
        return (config.PrincipalEntity, config.DependentEntity, string.Join(",", config.ForeignKeyProperties));
    }
```

This requires adding `using System.Linq;` to the top of `DiagramModelBuilder.cs` if not already present (check first — `entityResult.Value.Select(...)` already implies it's there).

- [ ] **Step 4: Run the `DiagramModelBuilderTests` suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~DiagramModelBuilderTests"`

Expected: PASS — all 4 tests.

- [ ] **Step 5: Run both full test projects**

Run: `dotnet test`

Expected: PASS across `EfSchemaVisualizer.Core.Tests` and `EfSchemaVisualizer.Web.Tests` — no regressions in `DiagramSyncTests`, `EntityNodeAccessibilityTests`, or any `Core` suite.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Union annotation-derived relationships into DiagramModelBuilder, fluent wins on conflict"
```

---

## Final verification

- [ ] **Run the complete test suite one more time**

Run: `dotnet test`

Expected: 100% pass, no warnings introduced. Note the new total test count (was 311/311 at `6a98131`) in the final commit or PR description for continuity with the backlog's running tally.

- [ ] **Update the backlog**

Modify `docs/backlog.md`: mark the three Round 2 Priority 0 items (`Data-annotation configuration is completely unread`, `Duplicate entity (class) names collide silently`, `Nested type declarations are dropped without a diagnostic`) as `[x]` with an `**Update:**` note referencing this plan/design and the new test count, following the exact style of every other closed item in that file.
