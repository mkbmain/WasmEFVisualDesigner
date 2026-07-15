# Editable-Diagram Browser Verification — Results

## What was run

A Playwright-driven Chromium session against a `dotnet publish` build of
`EfSchemaVisualizer.Web`, served locally via `python3 -m http.server`. One
representative gesture per editable-diagram phase was exercised against the
app's shipped default sample data (Blog/Post), reading back the regenerated
class/config source after each gesture to confirm correctness — not just
that the UI didn't crash. Full script: see
`2026-07-15-pages-deploy-and-browser-verification.md` Task 3 for the source
(not committed to the repo; ad hoc for this session, lived under a scratchpad
directory).

No screenshots from the session are committed to the repo or otherwise
retained anywhere durable — any screenshots taken during the run lived only
in the ephemeral scratchpad and are not available as evidence going forward.
The evidence of record is the script's console output (reproduced below) and
the source-code diffs of the resulting commits.

## Results

All five phases passed on the final run (exit code 0):

- **Phase 1 (rename):** pass — renaming the `Blog` entity to `BlogPost`
  produced a class source with `class BlogPost` and a config source
  referencing `Entity<BlogPost>`, with the old `Blog` name gone from the
  class source.
- **Phase 2 (add/remove property):** pass — adding `NewProperty` to
  `BlogPost` and removing `Title` from `Post` were both reflected correctly
  in the regenerated class source.
- **Phase 3 (primary key toggle):** pass — toggling `NewProperty` to be part
  of the primary key produced a composite `HasKey` call including
  `NewProperty` in the config source.
- **Phase 4 (table mapping):** pass — setting a table mapping produced
  `ToTable("blog_posts")` for `BlogPost` in the config source.
- **Phase 5 (relationship drag-connect):** pass — adding a new entity
  (`NewEntity`) and drag-connecting it to `BlogPost` produced a `NewEntity`
  class in the class source and a relationship from `NewEntity` to
  `BlogPost` in the config source, with no browser console errors during the
  interaction.

Console output from the final run:

```
Loading app...
Rendering diagram from default sample data...

--- Phase 1: rename entity ---
OK: class source renamed to BlogPost
OK: config source references Entity<BlogPost>
OK: old class name Blog is gone

--- Phase 2: add/remove property ---
OK: NewProperty added to BlogPost
OK: Title removed from Post class

--- Phase 3: toggle primary key ---
OK: HasKey composite key includes NewProperty

--- Phase 4: table mapping ---
OK: ToTable("blog_posts") written for BlogPost

--- Phase 5: relationship drag-connect ---
OK: NewEntity class added
OK: both ports have bounding boxes
OK: relationship from NewEntity to BlogPost written to config source
OK: no browser console errors (found: [])

All five phases verified successfully.
```

## Bugs found and fixed

Three real bugs in `src/` were found and fixed while getting the script to a
passing state (in addition to two script-only selector bugs in the ad hoc
`verify.js`, which were not app defects and are not detailed here):

1. **Missing accessible names on icon-only buttons** —
   `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`. The "Remove
   entity", "More options", "Remove property", and "Remove index" buttons
   had `title="..."` attributes but no `aria-label`, so their accessible
   name resolved to glyph text (`"×"`, `"▸"`) rather than a meaningful label
   — a real accessibility bug independent of this verification task, not
   just a Playwright inconvenience. Fixed by adding matching `aria-label`
   attributes to all four buttons. Commit `f326804`.

2. **Diagram edits destroyed and recreated every entity-node component on
   every edit, wiping local UI state** —
   `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs` and
   `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs`. `DiagramSync.
   Rebuild` cleared and reconstructed a brand-new `EntityNodeModel` for
   every entity on every edit; because the underlying `Blazor.Diagrams`
   library keys each node's Razor component by the model object's own
   reference identity, replacing the instance destroyed and recreated the
   `EntityNode` component for every card on every edit — wiping
   component-local state such as an expanded property panel, even for
   entities unrelated to the edit. Fixed by looking up existing
   `EntityNodeModel` instances by their stable `EntityId` and updating them
   in place (only genuinely new/removed entities get new/removed node
   objects). Commit `0e7f528`.

3. **"+ Entity" crashed on the app's own shipped bare-fluent-config sample
   data** — `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`.
   `AddEntity` always looked for an `OnModelCreating` method to attach a new
   `DbSet<T>` property to, throwing when none existed — which is exactly the
   shape of the app's own default sample data (bare top-level
   `modelBuilder.Entity<T>(...)` statements, no wrapping method or class).
   Initially fixed in commit `c93d444` by appending the new entity's block
   as a bare top-level statement (skipping the `DbSet<T>` property, since
   there is no class to attach it to) — but that fix hardcoded the receiver
   identifier as `modelBuilder`, which would silently produce uncompilable
   output for any bare source using a different receiver name (e.g.
   `builder`). A task review caught this as a latent bug; it was fixed
   properly in commit `4c06852` by reading the receiver name off an existing
   `receiver.Entity<T>(...)` invocation in the source (falling back to
   `modelBuilder` only when no such invocation exists), with a new
   regression test (`AddEntity_BareFluentConfigSourceWithNonDefaultReceiverName_UsesExistingReceiverNotModelBuilder`)
   added to cover it.

`dotnet test EfSchemaVisualizer.slnx --configuration Release` stayed green
throughout (303/303 passing after the final fix; 302 pre-existing + 1 new
regression test). Note that `src/EfSchemaVisualizer.Web` has no test project
of any kind in this repo, so bugs 1 and 2 (both in `Web`) have no automated
regression coverage — a pre-existing gap, not something introduced or
resolved by this task.

## Scope not covered

Only one gesture per phase was exercised (per the approved verification
design). Exhaustive coverage of every editing gesture (e.g. index
create/rename/remove, precision/scale, default values, all four
relationship shapes, `IEntityTypeConfiguration<T>` style) was not attempted
and remains open if wanted later.
