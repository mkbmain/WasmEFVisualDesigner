# Value Converters & Enum Storage — Design

> Backlog item: "Value converters and enums: `HasConversion` (all overloads),
> `HasConversion<string>()` on enum properties. Enum properties currently
> render as their bare CLR type with no indication of how they're stored."
> (`docs/backlog.md`, Priority 2)

## Goal

Parse, model, and (where feasible) edit EF Core's `HasConversion` fluent API,
and always surface how an enum-typed property is actually stored — even when
no `HasConversion` call is present (EF's convention default: the enum's
underlying numeric type, `int` unless declared otherwise).

## Scope

Two `HasConversion` overload shapes are recognized and modeled:

1. **Type-only**: `HasConversion<TProvider>()` and `HasConversion(typeof(TProvider))`.
   Fully editable — the diagram can set, change, or clear these.
2. **Lambda-pair**: `HasConversion(convertToProviderExpr, convertFromProviderExpr)`.
   Recognized and displayed (not flagged as unrecognized), but read-only — no
   rewriter path to create, change, or remove one. Inferring a lambda's return
   type is out of scope.

Any other shape (a `ValueConverter` instance argument, a single-lambda call,
wrong argument count, an unresolvable type argument) falls back to a new
`UnreadableHasConversionArgument` diagnostic — same fallback pattern as other
features in this codebase (e.g. `UnreadableHasDefaultValueSqlArgument`).

This applies to **any property type**, not just enums — enum properties are a
special case that additionally get a default-storage annotation even with no
`HasConversion` at all.

Out of scope (documented as non-goals, not defects):
- `ValueConverter` instance overloads (`new SomeValueConverter()`).
- `ConverterMappingHints`.
- Inferring a lambda conversion's provider type.
- Editing or removing a lambda-form conversion.

## Model changes

`PropertyModel` (`src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`) gains:

- `string? ConversionProviderClrType = null` — provider type from a type-only
  `HasConversion` call.
- `bool? ConversionIsCustomLambda = null` — `true` when a two-lambda
  `HasConversion` call is present.
- `bool IsEnumType = false` — `true` when the property's declared CLR type
  matches an enum declared anywhere in the parsed source.
- `string? EnumUnderlyingClrType = null` — the enum's underlying type
  (`"int"` unless the enum declares an explicit base, e.g. `enum Foo : byte`).
  Only set when `IsEnumType` is `true`.

`ConversionProviderClrType`/`ConversionIsCustomLambda` and
`IsEnumType`/`EnumUnderlyingClrType` are independent: a property can be an
enum with no explicit conversion (default-storage annotation only), an enum
with an explicit conversion (explicit wins, shown instead of the default), or
a non-enum property with an explicit conversion (no enum annotation at all).

## Enum detection

`EntityClassParser` already walks every `TypeDeclarationSyntax` across all
parsed files in a single pass to resolve sibling base types for
`BaseEntityName` resolution. Extend that same pass to also collect
`EnumDeclarationSyntax` nodes into a `Dictionary<string, string>` keyed by
enum name, valued by underlying type text (default `"int"` when the enum's
base list is empty). This dictionary is returned alongside the existing
class-parsing results and threaded into `DiagramModelBuilder.Build`.

## Parsing

New `FluentConfigParser.ParseValueConversions`, structured like the existing
`ParseComputedColumnSqls`: iterate `FluentSyntaxHelpers.FindConfigurationScopes`,
then `FluentSyntaxHelpers.FindCallsNamed(scope, "HasConversion")`, resolve the
target property via `GetPropertyNameFor` (emitting `UnresolvablePropertyName`
if it can't be resolved, same as other extractors), then classify the call:

- Generic type argument (`HasConversion<T>()`) or a single `typeof(T)`
  argument → `ValueConversionConfig { EntityName, PropertyName, ProviderClrType }`.
- Exactly two lambda arguments (`SimpleLambdaExpressionSyntax` or
  `ParenthesizedLambdaExpressionSyntax`) → `ValueConversionConfig { EntityName,
  PropertyName, IsCustomLambda = true }`.
- Anything else → `UnreadableHasConversionArgument` diagnostic, no config
  emitted for that call.

`HasConversion` is added to `FluentConfigParser.RecognizedCallNames`, so these
are the only two shapes that stop producing the generic `UnrecognizedConfigCall`
diagnostic; the unreadable-argument fallback above preserves diagnostic
coverage for the shapes this pass doesn't model.

## Merging & inference

`ModelMerger.ApplyValueConversions(entity, configs)` — same shape as
`ApplyComputedColumnSqls`: index configs by property name, set
`ConversionProviderClrType`/`ConversionIsCustomLambda` on the matching
property, leave others unchanged.

New `EfSchemaVisualizer.Core.Inference.EnumStorageInference.Fold` runs in
`DiagramModelBuilder.Build` after `ApplyValueConversions` (and after
inheritance/owned folding, so it sees final property lists): for every
property whose `ClrType` matches a key in the enum dictionary collected
above, set `IsEnumType = true` and `EnumUnderlyingClrType` from the
dictionary — regardless of whether `ConversionProviderClrType` is already
set. Explicit conversion and default-storage annotation are both present in
the model simultaneously; it's a rendering decision (below) to show the
explicit one when present.

## Rewriting

`OnModelCreatingRewriter.SetValueConversion(source, entityName, propertyName,
providerClrType)` / `RemoveValueConversion(source, entityName, propertyName)`
— follow the existing `SetComputedColumnSql`/`RemoveComputedColumnSql`
cascade: mutate an existing `HasConversion` call in place if found; else
append `.HasConversion(...)` to an existing `Property(...)` call for that
property; else insert a new statement into an existing entity scope; else
create a new entity configuration block. Both also get owned-property
variants (`SetValueConversionOnOwnedProperty`/`RemoveValueConversionOnOwnedProperty`)
mirroring the existing owned-property helpers, since folded owned/complex
properties already support this pattern for other single-value edits.

No rewriter method is added for the lambda-pair form — it is display-only.

## Editing

`DiagramEditor.SetValueConversion(entityName, propertyName, providerType)`:
- Resolve the declaring entity via `DeclaringEntityName` (same routing as
  other scalar-property edits, so editing an inherited/owned property from a
  derived/owner card rewrites the correct source).
- Validate `providerType` with the existing `IsValidTypeToken` helper (the
  same guard `ChangePropertyType` uses) — reject malformed type tokens with an
  inline error instead of writing uncompilable source.
- No-op if unchanged.
- Empty/null `providerType` calls `RemoveValueConversion` instead.
- Branch on `FoldKind`/`OwnerNavigationProperty` to call the owned-property
  rewriter variants when needed, same as `SetComputedColumnSql`.

No editor method is added for the lambda-pair form.

## UI (`EntityNode.razor`)

Add a "Stored as" row per property:
- A text input, backed by a `<datalist>` of common provider types (`string`,
  `int`, `long`, `short`, `byte`), bound via `@onchange` through `SafeEdit` to
  `EditContext.Editor.SetValueConversion(...)` — same wiring pattern as the
  existing sequence-name input. Clearing the field removes the conversion.
- When `ConversionIsCustomLambda` is `true`, render a read-only "custom
  conversion" label instead of the input (no edit control).
- When `IsEnumType` is `true` and there's no explicit
  `ConversionProviderClrType`, show a muted hint next to the property's type
  — e.g. `int (default)` or the real `EnumUnderlyingClrType` — consistent
  with the existing muted styling used for inferred keys/relationships.

## Diagnostics

New `DiagnosticCodes.UnreadableHasConversionArgument` — emitted when a
`HasConversion` call's arguments don't match either recognized shape.
Category `Parse` (default), consistent with other "couldn't read this syntax"
codes.

## Testing

Mirrors the existing per-layer test files, one positive case per shape plus
the unreadable fallback:
- `FluentConfigParserTests` — type-argument form, `typeof(T)` form, lambda-pair
  form, and the unreadable-argument fallback (e.g. `ValueConverter` instance
  argument).
- `ModelMergerTests` — `ApplyValueConversions` sets fields correctly.
- `OnModelCreatingRewriterTests` — `SetValueConversion`/`RemoveValueConversion`,
  including the owned-property variants.
- `DiagramEditorTests` — `SetValueConversion` validation (invalid type token
  rejected), no-op on unchanged value, removal on empty input.
- `DiagramModelBuilderTests` (or a new validity-style test file) — an enum
  property with no `HasConversion` gets `IsEnumType`/`EnumUnderlyingClrType`
  set from its declared enum's base type; an enum property with an explicit
  `HasConversion<string>()` shows the explicit conversion.

Existing tests in `FluentConfigParserTests` that assert `HasConversion` is
currently flagged as `UnrecognizedConfigCall` (the type-only, lambda-pair, and
various chain-position cases) need updating to assert the new structured
parse behavior instead, except for shapes intentionally left unrecognized
(e.g. a `ValueConverter` instance argument), which should now assert
`UnreadableHasConversionArgument` instead of the generic
`UnrecognizedConfigCall`.
