# GitHub Pages Deploy + Editable-Diagram Browser Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a GitHub Actions workflow that tests, publishes, and deploys the Blazor WASM app to GitHub Pages on push to `main`; and run a real Chromium-driven pass through one gesture from each of the five editable-diagram phases against a locally published build, fixing any bug it finds and recording the results.

**Architecture:** Part 1 is a single new workflow file using standard `actions/upload-pages-artifact` + `actions/deploy-pages`, gated by `dotnet test`. Part 2 uses Node.js + Playwright (installed ad hoc in the scratchpad, not committed to the repo) driving Chromium against `dotnet publish` output served by `python3 -m http.server`, scripted to exercise rename, add/remove property, key toggle, table mapping, and drag-to-connect relationship gestures, verifying the regenerated C# source after each.

**Tech Stack:** GitHub Actions (`actions/checkout`, `actions/setup-dotnet`, `actions/upload-pages-artifact`, `actions/deploy-pages`), .NET 10 SDK, Node.js 20 + Playwright (Chromium), Python 3 `http.server`.

## Global Constraints

- .NET SDK: `10.0.x` (repo currently on `10.0.201`).
- Solution file: `EfSchemaVisualizer.slnx` at repo root — use this with `dotnet test`/`dotnet publish`, not per-project paths, except where a single project must be targeted (`dotnet publish` needs the Web project specifically).
- GitHub Pages target repo: `mkbmain/WasmEFVisualDesigner`, served at `https://mkbmain.github.io/WasmEFVisualDesigner/` — base href must become `/WasmEFVisualDesigner/` in the *published artifact only*; `src/EfSchemaVisualizer.Web/wwwroot/index.html` in source control keeps `<base href="/" />` for local dev.
- Playwright/Node tooling is ad hoc for this session only — do not add `package.json`, `node_modules`, or any JS test infra to the repo.
- Any bug the verification pass finds must be fixed in `src/`, with the existing `dotnet test` suite still green afterward, before the pass is considered complete.

---

### Task 1: GitHub Pages deploy workflow

**Files:**
- Create: `.github/workflows/deploy.yml`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing consumed by later tasks — this task is self-contained.

- [ ] **Step 1: Create the workflow file**

```yaml
name: Deploy to GitHub Pages

on:
  push:
    branches: [main]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Test
        run: dotnet test EfSchemaVisualizer.slnx --configuration Release

      - name: Publish
        run: dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o publish

      - name: Patch base href for GitHub Pages subpath
        run: sed -i 's#<base href="/" />#<base href="/WasmEFVisualDesigner/" />#' publish/wwwroot/index.html

      - name: Disable Jekyll processing
        run: touch publish/wwwroot/.nojekyll

      - name: Upload Pages artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: publish/wwwroot

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

- [ ] **Step 2: Validate YAML syntax**

Run: `python3 -c "import yaml, sys; yaml.safe_load(open('.github/workflows/deploy.yml'))" 2>&1 || python3 -c "import json,sys; print('yaml module unavailable, skipping')"`

Expected: no parse error printed (if the `yaml` module isn't installed, that's fine — the step is a syntax sanity check, not a hard requirement).

- [ ] **Step 3: Prove each shell step works by running it locally against the real repo**

```bash
dotnet test EfSchemaVisualizer.slnx --configuration Release
dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o /tmp/pages-workflow-check
sed -i 's#<base href="/" />#<base href="/WasmEFVisualDesigner/" />#' /tmp/pages-workflow-check/wwwroot/index.html
grep -o '<base href="[^"]*" />' /tmp/pages-workflow-check/wwwroot/index.html
touch /tmp/pages-workflow-check/wwwroot/.nojekyll
ls /tmp/pages-workflow-check/wwwroot/.nojekyll
rm -rf /tmp/pages-workflow-check
```

Expected: `dotnet test` reports all tests passing; `dotnet publish` succeeds; the `grep` line prints `<base href="/WasmEFVisualDesigner/" />`; the `ls` line prints the `.nojekyll` path with no "No such file" error.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/deploy.yml
git commit -m "Add GitHub Actions workflow to deploy the Blazor WASM app to GitHub Pages"
```

- [ ] **Step 5: Note the manual repo-settings step for the user**

After this commit is pushed, GitHub Settings → Pages → Source must be set to "GitHub Actions" for the workflow's `deploy` job to succeed (currently unset). Flag this to the user in the session — it cannot be done from within the repo or this plan.

---

### Task 2: Install ad hoc Node.js + Playwright tooling

**Files:**
- Create (scratchpad only, not the repo): `<scratchpad>/tools/node/` (extracted Node.js binary), `<scratchpad>/verify/package.json`, `<scratchpad>/verify/node_modules/`

**Interfaces:**
- Consumes: nothing.
- Produces: a `node` / `npm` / `npx` on `PATH` for the rest of this session, and an installed `playwright` package with the Chromium browser downloaded, both used by Task 3.

- [ ] **Step 1: Download and extract a self-contained Node.js runtime**

```bash
SCRATCH=/tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/9bdd6883-00ad-4aa2-985c-874efcab184b/scratchpad
mkdir -p "$SCRATCH/tools"
curl -sL https://nodejs.org/dist/v20.18.1/node-v20.18.1-linux-x64.tar.xz -o "$SCRATCH/tools/node.tar.xz"
tar -xJf "$SCRATCH/tools/node.tar.xz" -C "$SCRATCH/tools"
mv "$SCRATCH/tools/node-v20.18.1-linux-x64" "$SCRATCH/tools/node"
export PATH="$SCRATCH/tools/node/bin:$PATH"
node --version
npm --version
```

Expected: `node --version` prints `v20.18.1`; `npm --version` prints a `10.x` version string.

- [ ] **Step 2: Install Playwright and the Chromium browser**

```bash
SCRATCH=/tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/9bdd6883-00ad-4aa2-985c-874efcab184b/scratchpad
export PATH="$SCRATCH/tools/node/bin:$PATH"
mkdir -p "$SCRATCH/verify"
cd "$SCRATCH/verify"
npm init -y
npm install playwright@1.48.0
npx playwright install --with-deps chromium
```

Expected: `npm install` completes with no error; `npx playwright install --with-deps chromium` downloads Chromium and reports success (it may also apt-install OS-level dependencies, which succeeds since this session has root).

- [ ] **Step 3: Smoke-test the browser launches**

```bash
SCRATCH=/tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/9bdd6883-00ad-4aa2-985c-874efcab184b/scratchpad
export PATH="$SCRATCH/tools/node/bin:$PATH"
cd "$SCRATCH/verify"
node -e "
const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  await page.goto('data:text/html,<h1>ok</h1>');
  console.log(await page.textContent('h1'));
  await browser.close();
})();
"
```

Expected: prints `ok` with no errors.

No commit for this task — nothing here touches the repo.

---

### Task 3: Serve the published app and script the five-phase verification pass

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor` (add `id` attributes to the two source textareas and the "Render Diagram" button so the script can target them reliably — everything else is targeted by visible text, which is already stable)
- Create (scratchpad only): `<scratchpad>/verify/publish/` (published app), `<scratchpad>/verify/verify.js` (Playwright script), `<scratchpad>/verify/screenshots/`

**Interfaces:**
- Consumes: `node`/`npx` on `PATH` and the installed `playwright` package from Task 2.
- Produces: pass/fail console output and screenshots consumed by Task 4's results note.

- [ ] **Step 1: Add stable element ids to `Home.razor`**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, update the two textareas and the render button (currently around lines 24, 28, 33):

```razor
        <textarea id="class-source" @bind="_classSource" @bind:event="oninput" @bind:after="SyncEditorSource" rows="14" style="width: 100%; font-family: monospace;"></textarea>
```

```razor
        <textarea id="config-source" @bind="_configSource" @bind:event="oninput" @bind:after="SyncEditorSource" rows="14" style="width: 100%; font-family: monospace;"></textarea>
```

```razor
    <button id="render-diagram" class="btn btn-primary" @onclick="RenderDiagram">Render Diagram</button>
```

- [ ] **Step 2: Run the existing test suite to confirm the markup change didn't break anything**

Run: `dotnet test EfSchemaVisualizer.slnx --configuration Release`
Expected: `Passed!` with the same 302-test count as before (the Web project has no tests of its own; this confirms `Core` is untouched and the solution still builds).

- [ ] **Step 3: Commit the markup change**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Add stable element ids to Home.razor for browser-driven verification"
```

- [ ] **Step 4: Publish the app and serve it locally**

```bash
SCRATCH=/tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/9bdd6883-00ad-4aa2-985c-874efcab184b/scratchpad
dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o "$SCRATCH/verify/publish"
cd "$SCRATCH/verify/publish/wwwroot"
python3 -m http.server 8085 &
sleep 1
curl -sI http://localhost:8085/index.html | head -1
```

Expected: `dotnet publish` succeeds; `curl` prints `HTTP/1.0 200 OK` (or `HTTP/1.1 200 OK`). Leave the server running in the background for the rest of this task.

- [ ] **Step 5: Write the verification script**

Create `<scratchpad>/verify/verify.js`:

```javascript
const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

const BASE_URL = 'http://localhost:8085';
const SCREENSHOT_DIR = path.join(__dirname, 'screenshots');

function assert(condition, message) {
  if (!condition) {
    throw new Error(`ASSERTION FAILED: ${message}`);
  }
  console.log(`OK: ${message}`);
}

async function getSources(page) {
  const classSource = await page.inputValue('#class-source');
  const configSource = await page.inputValue('#config-source');
  return { classSource, configSource };
}

(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  const consoleErrors = [];
  page.on('console', msg => {
    if (msg.type() === 'error') consoleErrors.push(msg.text());
  });

  console.log('Loading app...');
  await page.goto(BASE_URL);
  await page.waitForSelector('#render-diagram', { state: 'visible', timeout: 30000 });
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-loaded.png') });

  console.log('Rendering diagram from default sample data...');
  await page.click('#render-diagram');
  await page.waitForSelector('.card', { timeout: 10000 });
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-rendered.png') });

  // Phase 1: rename the Blog entity to BlogPost via double-click.
  console.log('\n--- Phase 1: rename entity ---');
  const blogHeaderSpan = page.locator('.card-header span', { hasText: 'Blog' }).first();
  await blogHeaderSpan.dblclick();
  const nameInput = page.locator('.card-header input').first();
  await nameInput.fill('BlogPost');
  await nameInput.press('Enter');
  await page.waitForTimeout(300);
  let sources = await getSources(page);
  assert(sources.classSource.includes('class BlogPost'), 'class source renamed to BlogPost');
  assert(sources.configSource.includes('Entity<BlogPost>'), 'config source references Entity<BlogPost>');
  assert(!sources.classSource.includes('class Blog\n') && !sources.classSource.includes('class Blog{'), 'old class name Blog is gone');
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-renamed-entity.png') });

  // Phase 2: add a property to BlogPost, then remove Post's Title property.
  console.log('\n--- Phase 2: add/remove property ---');
  const blogPostCard = page.locator('.card', { has: page.locator('.card-header', { hasText: 'BlogPost' }) });
  await blogPostCard.getByText('+ Add property').click();
  await page.waitForTimeout(300);
  sources = await getSources(page);
  assert(sources.classSource.includes('NewProperty'), 'NewProperty added to BlogPost');

  const postCard = page.locator('.card', { has: page.locator('.card-header', { hasText: 'Post' }) });
  const titleRow = postCard.locator('li', { hasText: 'Title' });
  await titleRow.getByRole('button', { name: 'Remove property' }).click();
  await page.waitForTimeout(300);
  sources = await getSources(page);
  const postClassMatch = sources.classSource.match(/class Post[\s\S]*?\n}/);
  assert(postClassMatch && !postClassMatch[0].includes('Title'), 'Title removed from Post class');
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-add-remove-property.png') });

  // Phase 3: toggle NewProperty as a primary key on BlogPost.
  console.log('\n--- Phase 3: toggle primary key ---');
  const newPropertyRow = blogPostCard.locator('li', { hasText: 'NewProperty' });
  await newPropertyRow.getByRole('button', { name: 'More options' }).click();
  await newPropertyRow.getByLabel('primary key').check();
  await page.waitForTimeout(300);
  sources = await getSources(page);
  assert(/HasKey\(e => new \{ e\.Id, e\.NewProperty \}\)|HasKey\(e => new \{ e\.NewProperty, e\.Id \}\)/.test(sources.configSource),
    'HasKey composite key includes NewProperty');
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '05-toggle-key.png') });
  // Undo the key toggle so it doesn't interfere with later assertions on BlogPost.
  await newPropertyRow.getByLabel('primary key').uncheck();
  await page.waitForTimeout(300);

  // Phase 4: set a table name on BlogPost.
  console.log('\n--- Phase 4: table mapping ---');
  const tableInput = blogPostCard.locator('input[placeholder="(default)"]').first();
  await tableInput.fill('blog_posts');
  await tableInput.press('Tab');
  await page.waitForTimeout(300);
  sources = await getSources(page);
  assert(sources.configSource.includes('ToTable("blog_posts")'), 'ToTable("blog_posts") written for BlogPost');
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '06-table-mapping.png') });

  // Phase 5: add a new entity and drag-connect it to BlogPost to create a relationship.
  console.log('\n--- Phase 5: relationship drag-connect ---');
  await page.getByRole('button', { name: '+ Entity' }).click();
  await page.waitForTimeout(300);
  sources = await getSources(page);
  assert(sources.classSource.includes('class NewEntity'), 'NewEntity class added');

  const newEntityCard = page.locator('.card', { has: page.locator('.card-header', { hasText: 'NewEntity' }) });
  const sourcePort = newEntityCard.locator('.entity-port').nth(1); // right port
  const targetPort = blogPostCard.locator('.entity-port').nth(0); // left port
  const sourceBox = await sourcePort.boundingBox();
  const targetBox = await targetPort.boundingBox();
  assert(sourceBox && targetBox, 'both ports have bounding boxes');

  await page.mouse.move(sourceBox.x + sourceBox.width / 2, sourceBox.y + sourceBox.height / 2);
  await page.mouse.down();
  await page.mouse.move(targetBox.x + targetBox.width / 2, targetBox.y + targetBox.height / 2, { steps: 10 });
  await page.mouse.up();
  await page.waitForTimeout(500);
  sources = await getSources(page);
  assert(/HasOne\(e => e\.BlogPost\)|HasOne<BlogPost>/.test(sources.configSource),
    'relationship from NewEntity to BlogPost written to config source');
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '07-relationship.png') });

  assert(consoleErrors.length === 0, `no browser console errors (found: ${JSON.stringify(consoleErrors)})`);

  console.log('\nAll five phases verified successfully.');
  await browser.close();
})().catch(async (err) => {
  console.error(err);
  process.exit(1);
});
```

- [ ] **Step 6: Run the verification script**

```bash
SCRATCH=/tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/9bdd6883-00ad-4aa2-985c-874efcab184b/scratchpad
export PATH="$SCRATCH/tools/node/bin:$PATH"
cd "$SCRATCH/verify"
node verify.js
```

Expected: each phase prints its `OK:` lines, ending with `All five phases verified successfully.` with exit code 0.

- [ ] **Step 7: Triage any failure**

If any assertion fails or the browser throws, read the error, reproduce the specific gesture manually against `src/` (via the existing unit tests or a focused repro), identify whether the bug is in `EfSchemaVisualizer.Core`, `DiagramEditor`, or the Razor markup, fix it, re-run `dotnet test EfSchemaVisualizer.slnx`, re-publish (Step 4), and re-run `node verify.js` until all five phases pass. Commit each fix separately with a message describing the bug found, e.g.:

```bash
git add -A
git commit -m "Fix <specific bug found during editable-diagram browser verification>"
```

(No fix is prescribed here since none is known yet — this step only applies if the run in Step 6 fails.)

- [ ] **Step 8: Stop the local server**

```bash
kill %1 2>/dev/null || pkill -f "http.server 8085"
```

---

### Task 4: Record results and close out the backlog item

**Files:**
- Create: `docs/superpowers/specs/2026-07-15-editable-diagram-browser-verification.md`
- Modify: `docs/backlog.md` (Priority 4, the editable-diagram entry's "Known gap across all merged phases" bullet)

**Interfaces:**
- Consumes: the console output and screenshot files from Task 3, Step 6.
- Produces: nothing consumed by later tasks — this is the final task.

- [ ] **Step 1: Write the results note**

Create `docs/superpowers/specs/2026-07-15-editable-diagram-browser-verification.md` with this structure (fill in the actual outcome — pass/fail per phase, any bugs found and fixed, referencing the commit(s) from Task 3 Step 7 if any — based on the real run from Task 3):

```markdown
# Editable-Diagram Browser Verification — Results

## What was run

A Playwright-driven Chromium session against a `dotnet publish` build of
`EfSchemaVisualizer.Web`, served locally via `python3 -m http.server`. One
representative gesture per editable-diagram phase was exercised against the
app's shipped default sample data (Blog/Post), reading back the regenerated
class/config source after each gesture to confirm correctness — not just
that the UI didn't crash. Full script: see
`2026-07-15-pages-deploy-and-browser-verification.md` Task 3 for the source
(not committed to the repo; ad hoc for this session).

## Results

- **Phase 1 (rename):** [pass/fail — describe]
- **Phase 2 (add/remove property):** [pass/fail — describe]
- **Phase 3 (primary key toggle):** [pass/fail — describe]
- **Phase 4 (table mapping):** [pass/fail — describe]
- **Phase 5 (relationship drag-connect):** [pass/fail — describe]

## Bugs found and fixed

[List each, with the fix commit hash, or "None — all five phases passed on
the first run."]

## Scope not covered

Only one gesture per phase was exercised (per the approved verification
design). Exhaustive coverage of every editing gesture (e.g. index
create/rename/remove, precision/scale, default values, all four
relationship shapes, `IEntityTypeConfiguration<T>` style) was not attempted
and remains open if wanted later.
```

- [ ] **Step 2: Update the backlog**

In `docs/backlog.md`, find the "Known gap across all merged phases" bullet under the editable-diagram Priority 4 item (currently the paragraph starting "interactive in-browser verification (drag, click-through the actual editing flows) has not been possible"). Replace it with:

```markdown
      - **Browser verification — done.** One representative gesture per
        phase was scripted and run against a real Chromium instance,
        confirming each produces correct regenerated source — see
        `2026-07-15-editable-diagram-browser-verification.md`.
```

Also change the two remaining Priority 4 checkboxes to reflect completion: the "GitHub Actions → GitHub Pages" line's `- [ ]` becomes `- [x]`, with an **Update:** note pointing at `.github/workflows/deploy.yml` and flagging that the "Pages source: GitHub Actions" repo setting still needs to be enabled manually (per Task 1, Step 5).

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs/2026-07-15-editable-diagram-browser-verification.md docs/backlog.md
git commit -m "Record editable-diagram browser verification results and close out Phase 4 backlog items"
```

- [ ] **Step 4: Tell the user the one remaining manual step**

Remind the user (in the session, not in any file) that GitHub Settings → Pages → Source must be switched to "GitHub Actions" before the workflow's `deploy` job will succeed, and that pushing this branch to `main` (or merging it) is what will trigger the first real deploy.
