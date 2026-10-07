# Sveta's Recipes

A Windows desktop app for Sveta (one user) that replaces her 2010-era MS Access
"Sveta Recipe Database": recipe costing for a home pastry business. Avalonia 12 + .NET 10, SQLite via EF Core,
PDF output via QuestPDF. The legacy system and its analysis live in `legacy/` (see `legacy/ANALYSIS.md`).

Decisions: single user, desktop (her PC is old → native beats a browser). **Every feature of the Access app is ported**,
including invoices and customers (an early "no invoices" decision was reversed 2026-10-05). The feature-by-feature audit
is in `legacy/ANALYSIS.md`; `docs/old-vs-new/` shows each legacy screen beside its replacement.

## Layout

```
src/Bolt.Theme             the app's theme (forked from andrew's aibolt 2026-10-07; owned here now, not shared)
src/SvetaRecipes.Core      domain model, EF Core (SQLite), costing/scaling/conversion maths, RecipeBook store, backups
src/SvetaRecipes.Reports   QuestPDF documents: recipe (scaled, sub-recipes expanded), label sheet, shopping list
src/SvetaRecipes.App       Avalonia UI (MVVM, CommunityToolkit.Mvvm), one tab per area + the assistant panel
tools/SvetaRecipes.Import  legacy .mdb → new database (needs mdbtools; Mac only)
tools/SvetaRecipes.Shots   headless smoke test + screenshots (no display needed); `check` = any-data screen check; `devmode` test
tests/SvetaRecipes.Core.Tests   engine tests + parity against the legacy data
CHANGELOG.md               release notes, one section per version (shown in the app under About)
release.sh                 test → publish → Velopack pack → (--upload) GitHub release, (--seed) private handoff installer
legacy/                    the Access files as received, extracted CSV/VBA/screenshots, ANALYSIS.md
```

## Styling: none in the app — Bolt.Theme only

The UI uses **Bolt.Theme** (`src/Bolt.Theme`, this repo's own copy of the theme from andrew's aibolt; changes are
made here and not synced back), built so an AI never has to invent styling. Views use only its vocabulary — `Border.card`,
`TextBlock.caption/title/h2/secondary/muted/small/mono`, `Button.accent/ghost/link/icon`, `ToggleButton.pill`,
`Border.hairline/vrule/badge/bubble`, `TextBox.composer`, `b:Icon` with `Bolt.Icon.*`, `b:MarkdownBlock`, `b:Spinner` —
plus layout (margins, grids, spacing).
**No `<Style>` or `UserControl.Styles` with visual setters in this repo.** The one `TreeView.Styles` entry binds
`IsExpanded` (data plumbing, not looks). If a control looks wrong, the fix belongs in Bolt.Theme: e.g. the
`AutoCompleteBox` rule added to `Bolt.Theme/Styles.axaml` on 2026-10-05 (Fluent template-binds its inner TextBox to a
grey `TextControlBackground`). DataGrid and TreeView use Fluent templates, which read Bolt's Fluent palette.
Default theme variant is **Light** (Settings offers Dark / System).

## Costing — the part that must stay right

`Core/Costing/CostCalculator.cs`, ported from the VBA (`legacy/extracted/vba/Form_frmRecipes.bas`
`CalculateItemCost`/`CalculateRecipeCost`, `modMiscFunctions.bas` `CalculateItemAmount`):
- ingredient line: amount × convert(line unit → ingredient unit) ÷ pack size × pack price
- **per-piece** ingredient: `PackPrice` = price of ONE piece, `Unit`/`PackQty` = weight of one piece (her data has
  `Unit = g`, lines in `pc`); cost = pieces × price, weight = pieces × PackQty
- sub-recipe line: (amount used ÷ sub-recipe total weight) × sub-recipe total cost **including its labour**
- recipe total weight = Σ lines converted to the recipe unit (derived, never typed); % = line weight ÷ total
- cost/serving: ÷ servings, or × weight-per-serving ÷ total weight (`CostMethod`)
- labour = hours × the global labour rate setting (legacy `tblVersion.LabourRate`, $20)

Deliberate differences from legacy (all were bugs):
1. **Costs are computed live, recursively.** Legacy cached `tblRecipes.Cost` and refreshed it only when a recipe was
   opened, so a price change never reached parent recipes. Parity test: 413 of 749 cached legacy costs are stale.
2. Unit conversions: direct, then the **reciprocal** of the reverse pair, then **two steps** through any unit
   (L → ml → g). Legacy silently used 1:1 when the exact pair was missing. Still-missing pairs that affect COST are
   warned about per line (⚠ icon + list under the grid); weight-only ones are not (legacy behaviour).
3. Shopping lists from recipes scale sub-recipe ingredients by the amount used and total duplicates (legacy listed
   them unscaled, one row per line).

Ground truth: `LegacyParityTests.Caramel_choc_cremeux_matches_the_legacy_screen` reproduces Sveta's screenshot of the
Access screen to the cent, line by line. Keep it green.

## Invoices and companies

- `Company` = legacy `tblClient`: one record can be a customer, a supplier or both (`IsCustomer` / `IsSupplier`).
  A company with invoices can't be deleted (legacy rule).
- `Invoice.Id` **is** the invoice number (legacy `InvoiceNo` kept on import; new ones are max + 1).
  `Total = Σ items − Discount + Tax1 + Tax2`; items are `Price × Quantity`. Legacy computed net − discount and never
  used its tax fields (all zero), so totals are identical. The parity test checks invoice count, total billed and
  total received against the legacy data (the legacy screen's footer).
- Invoice items pick a recipe at its **price per serving** (falls back to selling price) or take free text — as the
  legacy item combo did. Your business details (legacy `tblSystemSettings`) are settings, printed in the invoice header.

## Data / persistence

- `RecipeBook` holds the whole DB in memory (a few MB) and writes through: each save opens a short-lived
  `RecipesDbContext`, applies the change to a tracked copy, commits. UI code edits **clones** (`Model/Cloning.cs`)
  and calls `Save*`; never attach the in-memory lists to a context.
- DB: `%APPDATA%\Sveta's Recipes\recipes.db` (override with env `SVETA_RECIPES_DB`). **Never under
  `%LOCALAPPDATA%\SvetaRecipes`** — that is Velopack's install root and an uninstall deletes it. First start copies
  `seed/recipes.db` from next to the exe if no DB exists — never overwrites.
- Schema changes: add an EF migration (`cd src/SvetaRecipes.Core && dotnet ef migrations add <Name> -o Data/Migrations`);
  `RecipeBook.Open` migrates on start and snapshots the DB to `pre-upgrade/` first.
- Backups: SQLite online-backup API, one per day on start-up (background), last 30 kept, default folder
  `Documents\Sveta Recipes Backups` (follows OneDrive-redirected Documents). Restore from Tools & settings.
- Legacy ids are preserved on import, so a row can be traced to the Access file.

## Assistant panel

A collapsible chat column on the right (toggle top-right; open state in setting `AssistantOpen`). Same integration as
aibolt: the .NET Claude Agent SDK (NuGet `YetAnotherClaudeAgentSdk`, andrew's fork of 0xeb/claude-agent-sdk-dotnet,
github.com/macsux/claude-agent-sdk-dotnet; namespaces `Claude.AgentSdk`) runs
the **Claude Code CLI as a subprocess**, so her PC needs Claude Code installed and logged in. `Services/Assistant.cs`:
- Claude Code's own tools are on (web search/fetch, files, shell; `BypassPermissions`, so nothing asks), but no
  settings/CLAUDE.md/skills from the PC (`SettingSources = []`); its working folder is `assistant/` next to the DB.
  The prompt says to change the database only through `sql`. It also gets an in-process MCP server `recipes`: `sql` (any SQL, reads **and writes**, `Core/Assistant/SqlRunner.cs`),
  `backup` (snapshot; the prompt says to call it before the first write), `get_costing` (live `CostCalculator`),
  `find_similar` (`Core/Assistant/Similarity.cs`). Unit conversions are left to the model.
- System prompt = instructions + schema. The instructions (`Assistant.DefaultInstructions`) are editable under Tools &
  settings → Assistant (setting `AssistantPrompt`, stored only when changed; editing reconnects the chat on the next
  message). It must not import a recipe unless she explicitly asks.
- The schema is generated from the EF model by `Core/Assistant/DbSchema.cs` (enum values,
  money-as-TEXT, FKs). Hand-written notes there are only for what names don't say; keep them minimal — a test fails if
  a note points at a column that no longer exists.
- After any write the `sql` tool reloads the `RecipeBook` and `MainViewModel.DataChangedOutside` refreshes the open
  screens (the open recipe is reopened unless it has unsaved edits). Each message is prefixed with what's open.
- Recipe import is a chat workflow, no review screen, only when asked: map lines → `find_similar` (whole recipe and components) → the
  model decides duplicate (don't import) / variant / new, creates missing ingredients unpriced, tags "Imported".
- Similarity: every recipe flattened to leaf ingredients (sub-recipes expanded, scaled by amount used) as weight
  fractions; score = Σ min(shareA, shareB), + 0.1 × name-trigram similarity; `coverage` = share of the draft that maps
  to her ingredients. Snapshots (assistant, Backup now) are pruned separately from the 30 dailies.
- Every chat is saved as JSON lines in `assistant/chat-*.jsonl` next to the DB (`Services/Transcript.cs`: user text +
  what was open, replies, full tool inputs/results, the Claude session id). On start the latest is shown and its
  session resumed (a fresh one if resume fails); "+" starts a new chat. Tool calls show as expandable rows with the
  complete input and result; the wrench toggle in the panel header hides them (setting `AssistantShowTools`).
- **Point at part of the app** (experimental, unreleased): the pin button above Send puts `Views/PickOverlay`
  over the window (Snoop-style: hover outlines the element under the mouse, wheel = parent/child, click captures,
  Esc/right-click cancels). Hit testing is our own geometric walk (respects clipping, skips template parts). The capture
  is a marked screenshot + close-up (PNG in `assistant/picks/`) and a text description (view, element path, label,
  bindings via reflection on Avalonia's internal `Description`, DataContext). Several can be attached; each goes with the
  next message as its description + two image blocks.

## Development mode (experimental, unreleased)

The "Release / Development" pill next to Assistant. Development = the app runs from a build of a local clone of this
repo and the assistant is also the app's developer. `Services/DevMode.cs` + `ViewModels/DevViewModel.cs`:
- Source folder: the switch asks for it (default `~/Source/SvetaRecipes`, Browse, or type a path); changeable later
  under Tools & settings → Assistant (in development mode that rebuilds from the new folder and restarts). Stored as
  `SourceDir` in `dev-mode.json` (env `SVETA_RECIPES_SOURCE` overrides). An empty folder is cloned into; a folder that
  already has a checkout of this repo (andrew's Mac: `~/projects/macsux/sveta-recipes`) is used as it is: its branch,
  its changes, its owner's git identity. A running build finds its source from where it runs (`<source>/.builds/<build>`).
- Switching on is silent after that dialog: missing git / .NET 10 SDK are installed per-user with no admin and no
  windows (MinGit zip + `dotnet-install.ps1` into `%LOCALAPPDATA%\SvetaRecipesDev\tools`, put first on PATH by
  `DevMode.UseInstalledTools` at every start, inherited by builds and Claude Code); clones `Updates.RepoUrl` (env
  `SVETA_RECIPES_REPO`) at the running commit (else tag `v<version>`) on branch `local`, sets git identity/config if
  missing, builds, restarts into it. If a step fails, the error goes
  to the assistant (`ChatViewModel.RunForApp`, shown as a note) to fix silently, then setup is retried once.
- Versions: every build stamps its commit (`-p:SourceRevisionId`, also in release.sh); `Updates.Revision` /
  `VersionText` show it (mode pill, About), and each assistant message says the build and commit she's on.
- Commits: `prepare_changes` snapshots the working tree as a commit with a separate index (`git commit-tree`, message =
  the summary) without moving the branch, and builds that. Apply changes = `DevMode.CommitApplied` moves `local` to it
  (git index follows, later edits stay uncommitted), then restarts. The agent is told not to commit itself.
- Builds: `dotnet publish` to a NEW folder `.builds/<yyyyMMdd-HHmmss>/` each time (the running build is never
  overwritten, so the agent can `dotnet build`/test freely), then `Shots check` on a copy of her DB. Newest 3 kept.
- State `dev-mode.json` next to the DB (Enabled, Launcher = the installed exe, Current, LastGood, Tried). The installed
  app is only a launcher while Enabled (`DevMode.HandOver` first in `Main`); a build confirms itself 2 s after its
  window opens (`ConfirmStarted`). Apply = close normally (unsaved edits asked) → start new build → exit only once it
  confirms, else show the window again and tell the assistant why (crash text from `last-crash.txt`). A build that
  never confirmed is skipped by the launcher for LastGood.
- Assistant in dev mode: opus, cwd = the clone, `SettingSources = [Project]` (so this CLAUDE.md loads), the generated
  `Assistant.DevInstructions` (two roles; edit → build → `Shots check` + look at PNGs → commit → `prepare_changes`),
  and the `prepare_changes` tool (build + check, then the "Apply changes" bar). Its MCP timeout is 30 min.
- Test end to end (a local snapshot repo, opens real windows briefly; `--live` has Claude make a real change):
  `dotnet run --project tools/SvetaRecipes.Shots -- devmode .data/recipes.db <work folder> [--live]`

## UI gotchas (both cost real debugging)

- **Swapping a ContentControl's view-model leaks into the outgoing view.** Avalonia sets the presenter's DataContext to
  the new VM before replacing the child, so the old view's ComboBoxes/TextBoxes write stale values into the new VM.
  Always swap through `null` first (`RecipesViewModel.SetEditor`).
- **A TextBox writes back what it displays.** Values shown rounded (`0.###`, `0.00`) must be LOADED rounded
  (`R3`/`R2` in `RecipeEditorViewModel.Load`), or merely opening a recipe changes it (and re-derives servings).
  The smoke test checks "opening a recipe does not mark it modified".
- Grids save on `RowEditEnded` (code-behind) — like the Access datasheet. Check boxes/combos commit immediately.

## Self-contained

Everything builds from this repo plus nuget.org: no project, path or config above the repo root (development mode
clones it alone onto her PC). `NuGet.Config` clears inherited sources; the empty `Directory.Build.targets` and
`ImportDirectoryPackagesProps=false` stop MSBuild picking up files from parent folders. Keep it that way: a shared
library becomes a NuGet package or is copied in, never referenced as `../something`.

## Running

```sh
dotnet test --project tests/SvetaRecipes.Core.Tests          # engine + legacy parity (needs .data/recipes.db)
dotnet run --project tools/SvetaRecipes.Import -- legacy/original/SRD_data.mdb legacy/original/SRD_2018_u.mdb .data/recipes.db --force
SVETA_RECIPES_DB=$PWD/.data/dev.db dotnet run --project src/SvetaRecipes.App
dotnet run --project tools/SvetaRecipes.Shots -- .data/recipes.db .data/shots [Light|Dark]   # smoke test + PNGs
dotnet run --project tools/SvetaRecipes.Shots -- .data/recipes.db .data/shots-live Light --live   # + real assistant chats (CLI, costs a little)
dotnet run --project tools/SvetaRecipes.Shots -- check <any db> <out>      # every screen on a copy of any data; fails on errors
./release.sh 1.0.1 --upload                  # public update on GitHub (no data)
./release.sh 1.0.0 --upload --seed <SRD_data.mdb> <SRD_2018_u.mdb>   # + private installer with her data
```
On the Mac, the GUI fails with `Avalonia.Native was not able to start the RenderTimer … -6661` when the display is
asleep/locked (or from a sandboxed shell) — use the Shots tool, which renders headlessly with Skia.

## Releases, updates and her data

- **Every release has release notes in `CHANGELOG.md`**: a `## <version> — <date>` section listing everything that
  changed since the previous release (check `git log v<previous>..HEAD`), written for Sveta — plain words, what she
  will notice, no code talk. Write it before running `release.sh`, which refuses a version without a section and uses
  it as the GitHub release notes. The app shows the whole file under Tools & settings → About (embedded resource).

- **Public repo** github.com/macsux/sveta-recipes — **no user data, ever.** `legacy/` (her Access files, extracts,
  screenshots), `docs/old-vs-new/`, `.data/`, `publish/`, `Releases/` and every `*.db` are git-ignored. Test
  expectations about her data are read from the legacy dump at run time, not hard-coded.
- Updates: Velopack (`VelopackApp.Build().Run()` first thing in `Main`; `Services/Updates.cs`). The app checks the
  GitHub releases on start and every 30 minutes while running, and downloads in the background; then a bar at the top offers "Restart now" (closes the
  normal way, so unsaved edits are asked about, and relaunches) or "Later" (installed silently on exit). Releases are packed with
  `--delta None` because her install began from the seeded package (deltas against the public one wouldn't apply).
- Day one: `./release.sh <version> --upload --seed <SRD_data.mdb> <SRD_2018_u.mdb>` → public release on GitHub +
  `publish/handoff/Sveta's Recipes Setup <version>.exe`, the same version with her data as `seed/recipes.db`.
  Hand that file over privately. Later versions: `./release.sh <next> --upload` only.
- Framework-dependent: the app ships without .NET; Setup installs the .NET 10 runtime (from Microsoft) when missing
  (`--framework net10-x64-runtime`), so updates carry only app files.
- Installer is unsigned (cross-packed from macOS): SmartScreen shows "Windows protected your PC" → More info → Run
  anyway, once.

## Legacy features deliberately not ported

- `frmManageMenus` (a developer tool for Access command bars) and `Module1` (code from another project).
- Font *names* for the preview/layers (Options): only the recipe print text size is kept.

## Not done yet

- Assistant on her PC: Claude Code isn't installed or logged in there yet, and which account it uses (her subscription
  or an API key) is undecided. Without it the panel says Claude Code is missing.
- Label layout is a guess (2 × 5, Letter, Avery 5163-ish); the Access report layout could not be extracted.
