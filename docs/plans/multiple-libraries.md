# Implementation plan — Multiple libraries (separate catalogs)

Status: **proposed** · Target: a future minor (e.g. 0.9.0) · Decision (Paul, 2026-10-23): **Option B — separate catalogs.**

## Goal

Let the user keep **several independent libraries** and switch between them at runtime — each with its
own index, tags/notes, thumbnail cache, settings, and folder set. Like Lightroom catalogs: a *Work*
library and a *Personal* library never see each other's folders, tags, or search results.

(The lighter "named saved views inside one catalog" idea — Option A — was considered and set aside in
favour of true isolation. It could still be layered on later *inside* a catalog.)

## Why this is tractable

The data layer is already a clean seam:

- `LibraryService(string dataDir)` constructs **everything** — `ApertureDatabase`, all stores, the
  indexer, the watchers, and `SettingsService` — from one directory. It is `IDisposable`.
- `App.xaml.cs` already resolves the directory (`APERTURE_DATA_DIR` env override, else
  `AperturePaths.DefaultDataDir`) and builds `LibraryService` → `MainViewModel` once.
- A "library" therefore == **a data directory** (`aperture.db` + `thumbs.db` + `settings.json`).

So "multiple libraries" = *manage a set of data directories + swap the live `LibraryService` at runtime.*
No schema change to the existing DBs. The real work is the **catalog registry** and a **clean
teardown/re-init** of the service + view model.

## Design

### 1. Catalog registry (where the list of libraries lives)

A small JSON file **outside** any single catalog, at `%LOCALAPPDATA%\catalogs.json` — i.e. in
`%LOCALAPPDATA%` itself, the parent of the default data dir (`%LOCALAPPDATA%\Aperture`), **not** inside
any catalog dir. (Putting it inside the Default catalog would mean "Delete library files…" on Default
also wipes the registry.) It survives and is shared across catalogs:

```jsonc
{
  "catalogs": [
    { "id": "…guid…", "name": "Personal", "dir": "C:\\Users\\me\\AppData\\Local\\Aperture",      "lastOpenedTicks": … },
    { "id": "…guid…", "name": "Work",     "dir": "D:\\ApertureCatalogs\\Work",                    "lastOpenedTicks": … }
  ],
  "activeId": "…guid…"
}
```

- The **existing** `%LOCALAPPDATA%\Aperture` becomes the auto-registered **"Default"** catalog on first
  run after upgrade (zero migration — it's already a valid catalog dir).
- New class `Aperture.Core.Storage.CatalogRegistry` (plain JSON load/save; no FunkyORM — it's config,
  like `SettingsService`): `List()`, `Add(name, dir)`, `Rename(id, name)`, `Remove(id, deleteFiles:false)`,
  `SetActive(id)`, `Active`.
- `Remove` only forgets the catalog by default; deleting its files is a separate, explicitly-confirmed action.

### 2. Runtime switch (the load-bearing part)

The current app builds the service + VM once and never rebuilds. Switching catalogs means tearing the
old one down and standing a new one up **without leaking watchers/indexers or corrupting the DB**.

Add to `LibraryService` / `MainViewModel`:

- **Teardown (`LibraryService.Dispose` must be complete):** stop + dispose every `RootWatcher`
  (already tracked in `_watchers`), **cancel any in-flight index** (the pause/cancel token registry
  from 0.8.0 already exists — cancel-all on dispose), await those tasks settling, then dispose the DB
  connections. Add a `DisposeAsync`/`ShutdownAsync` so the switch can await indexers rather than racing
  a half-written transaction (retry/replay lens: a switch mid-index must not leave a torn write that the
  next open treats as committed).
- **Re-init:** the cleanest approach given WPF data-binding is to rebuild the VM and re-point the
  window's `DataContext`:
  1. `MainViewModel.PrepareForClose()` — cancel indexers, flush settings, drop event subscriptions.
  2. `oldLibrary.Dispose()` (await teardown).
  3. `new LibraryService(newDir)` → `new MainViewModel(...)`.
  4. `MainWindow.DataContext = newVm` (or raise an app-level `ActiveCatalogChanged` the window handles),
     then `newVm` runs its normal startup (load union off-thread, spinner, background reconcile).
- App holds `_library`/`_viewModel` as swappable fields (already fields in `App.xaml.cs`), plus the
  `CatalogRegistry`. A `SwitchCatalogAsync(id)` on `App` (or a dedicated `CatalogService`) owns the
  sequence above and is the single entry point.
- **Window state:** window placement/nav-width are per-catalog today (in each `settings.json`). Decide:
  keep window geometry global (move those keys to `catalogs.json` or a top-level settings) so switching
  doesn't jump the window. Low effort; do it.

### 3. UI

- **Title bar / command bar:** a catalog switcher — the active catalog name with a dropdown listing the
  others + **New library…**, **Open library…** (folder picker to an existing catalog dir or a new empty
  one), **Rename**, **Remove from list**, and (guarded) **Delete library files…**.
- **New library** flow: ask name + location (defaults to `%LOCALAPPDATA%\Aperture\Catalogs\<name>`),
  create the dir, register, switch to it (empty → first-run "Add folder").
- **Open library** points at any existing catalog dir (must contain/accept `aperture.db`).
- Show the active catalog name somewhere persistent (title or the nav header) so users always know which
  catalog they're in — the #1 Lightroom footgun is editing the wrong catalog.

### 4. Edge cases (the retry/replay + failure lens)

- **Switch while indexing:** cancel + await before swapping (above). Never dispose the DB with an open
  write transaction.
- **Catalog dir missing / unwritable at open:** surface a clear error, keep the previous catalog active,
  don't drop it from the registry.
- **Same dir opened twice / two app instances on one catalog:** SQLite WAL + `busy_timeout` tolerates
  reads; a second *writer* instance is already possible today — out of scope here, but note it.
- **`APERTURE_DATA_DIR` set:** when the env override is present, honour it as a pinned single catalog and
  hide the switcher (keeps the test/CI story unchanged).
- **Legacy "Reel" migration:** unchanged — it targets the default dir, which is just the Default catalog.

## Work breakdown (test-first)

1. `CatalogRegistry` + JSON round-trip + "register existing default on first run" — **unit tests**
   (add/rename/remove/setActive/load-missing-file/legacy-default). *Core, no UI.*
2. `LibraryService.ShutdownAsync` (cancel indexers, stop watchers, dispose) — **unit test** that a
   dispose mid-(simulated-)index cancels and leaves the DB consistent (open a fresh service on the same
   dir, assert readable + counts sane).
3. `App.SwitchCatalogAsync` orchestration + swappable `_library`/`_viewModel` + global window state.
4. Switcher UI + New/Open/Rename/Remove/Delete dialogs.
5. Manual verify: create a 2nd catalog, add a folder, switch back and forth, confirm tags/index are
   isolated; switch mid-index; open a missing dir; `APERTURE_DATA_DIR` still pins.

## Estimate / risk

- **Core (1–2):** ~1 focused session. Low risk; additive, no existing-DB migration.
- **Switch orchestration + UI (3–4):** ~1–2 sessions. The *only* real risk is the teardown/re-init race
  (indexers + watchers + DB), which item 2's async shutdown + test is built to cover.
- No change to the on-disk schema; existing users' single catalog becomes the Default catalog untouched.

## Out of scope (future)

- Named saved views **within** a catalog (Option A) — complementary, can layer on later.
- Moving/copying items *between* catalogs.
- A catalog-picker launch screen (vs. always opening the last-active catalog).
