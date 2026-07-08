# Diagnostics Channel & EntityClassParser Rewrite — Design

> Addresses the first slice of Priority 0 in `docs/backlog.md`: the
> diagnostics/"unsupported config" channel, plus the `EntityClassParser`
> correctness bugs whose fixes require the same return-type change
> (class-less file crash, single-class-only limitation, record/struct
> entities, property filtering).
>
> Explicitly **out of scope** for this pass: `FluentConfigParser` /
> `FluentSyntaxHelpers` fixes (hardcoded `modelBuilder` receiver, `Property`
> string-overload, non-literal `HasMaxLength` args). Those land as separate
> follow-up passes that consume the diagnostics channel established here.

## Motivation

The parser currently throws (`EntityClassParser.Parse` on a class-less file)
or silently drops information (unsupported fluent config) with no signal to
the caller. For a tool whose pitch is "upload your real project and trust
the output," both are trust-breaking. This pass establishes the diagnostics
channel and fixes the `EntityClassParser`-side correctness gaps that are the
same shape of change (its return type has to grow regardless of which fix
triggers it).

## 1. Diagnostics channel

New types, in `EfSchemaVisualizer.Core.Parsing`:

```csharp
public sealed record Diagnostic(
    string Code,           // stable id, e.g. "NoEntityDeclarations"
    string Message,        // human-readable, for future UI display
    string? EntityName,
    string? PropertyName,
    TextSpan Span);        // Roslyn source span of the offending syntax

public sealed record ParseResult<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics);
```

`TextSpan` is `Microsoft.CodeAnalysis.Text.TextSpan` (already an available
dependency via Roslyn). Where there's no single offending node (e.g. "file
has no entity declarations at all"), use the compilation unit root's span.

## 2. `EntityClassParser` rewrite

New signature:

```csharp
public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
```

Behavior:

- Walk the root for `ClassDeclarationSyntax`, `StructDeclarationSyntax`, and
  `RecordDeclarationSyntax` (covers `record`, `record class`, and
  `record struct`). Each becomes one `EntityModel`.
- **Class-less file** (no recognizable type declaration — e.g. only an
  `enum`, `interface`, or `delegate`): return an empty entity list plus one
  `Diagnostic("NoEntityDeclarations", ...)` with the root's span. No
  exception.
- **Non-entity type declarations** (`enum`, `interface`) alongside a real
  class/record/struct in the same file are simply skipped — no diagnostic,
  since skipping them isn't a loss of information.
- **Properties per entity** = `PropertyDeclarationSyntax` members **plus**
  primary-constructor parameters (for records with a parameter list),
  merged in declaration order: positional parameters first, then
  body-declared properties.
- **Property filtering** — a candidate property/parameter is excluded (no
  diagnostic — this is normal C#, not a parse gap) when:
  - it carries a `[NotMapped]` attribute (matched by attribute name
    `NotMapped` or `NotMappedAttribute`, syntactic match — no symbol
    resolution),
  - it is `static`,
  - it is get-only with no setter **and** is not a primary-constructor
    parameter (positional parameters are get-only by nature and must still
    be emitted).
- Expression-bodied / computed properties (`public string Full => ...`) are
  excluded under the get-only rule above (no `set`, not a positional
  parameter).

## 3. Downstream impact

- `ModelMerger.ApplyMaxLengths` is unchanged — it still takes one
  `EntityModel`; the caller loops over the list `EntityClassParser.Parse`
  now returns.
- `FluentConfigParser.ParseMaxLengths` is untouched in this pass. It keeps
  its current signature; its own diagnostics land in later passes.
- Existing tests (`EntityClassParserTests`, `RoundTripTests`) are updated
  for the new return shape as part of this change.

## 4. Testing

TDD — a failing test precedes each fix:

- Class-less file (only an `enum`) → empty entity list + one diagnostic, no
  exception.
- File with 2+ classes → 2+ `EntityModel`s.
- `record Product(int Id, string Name)` → 2 properties from positional
  params.
- `record class` with a positional parameter list *and* a body-declared
  property → merged list, positional-first order.
- `[NotMapped]` property → excluded.
- `static` property → excluded.
- Get-only property (`{ get; }`) → excluded.
- Expression-bodied property (`=>`) → excluded.
- Interface/enum declared alongside a real class in the same file → only
  the class becomes an entity; no diagnostic emitted for the interface/enum.
