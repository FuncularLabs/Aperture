# Implementation plan — Multiple libraries (separate catalogs)

Status: **built on branch `feat/multiple-libraries`, unreleased** (revision 2, after review round 1) ·
Target: a future minor (e.g. 0.9.0) · Decision (Paul, 2026-10-23): **Option B — separate catalogs.**
Rulings after review round 1 (Paul, 2026-10-25): **dedicated library folders**; **coverage: test the new
logic to ≥ 85 %, with `MainViewModel.cs`, `App.xaml.cs` and `MainWindow.xaml.cs` exempt** as pre-existing,
untested WPF shells.

## Goal

Let the user keep **several independent libraries** and switch between them at runtime — each with its
own index, tags/notes, thumbnail cache, settings, and folder set. Like Lightroom catalogs: a *Work*
library and a *Personal* library never see each other's folders, tags, or search results.

(The lighter "named saved views inside one catalog" idea — Option A — was considered and set aside in
favour of true isolation. It could still be layered on later *inside* a catalog.)

## Why this is tractable

The data layer is already a clean seam:

- `LibraryService(string dataDir)` constructs **everything** — `ApertureDatabase`, all stores, the
  indexer, the watchers, and `SettingsService` — from one directory.
- A "library" therefore == **a data directory** (`aperture.db` + `thumbs.db` + `settings.json`).

So "multiple libraries" = *manage a set of data directories + swap the live `LibraryService` at runtime.*
No schema change to the existing DBs. The real work is the **catalog registry** and a **clean
teardown/re-init** of the service + view model.

## Design

### 1. Catalog registry — `Aperture.Core.Storage.CatalogRegistry`

One JSON file, `%LOCALAPPDATA%\Aperture Libraries\catalogs.json` (`CatalogRegistry.DefaultPath`). It sits
in a sibling of the Default catalog's folder (`%LOCALAPPDATA%\Aperture`), never inside any catalog, so a
catalog's files and the list of every catalog never share a folder.

```jsonc
{
  "catalogs": [
    { "id": "…guid…", "name": "Default", "dir": "C:\\Users\\me\\AppData\\Local\\Aperture", "lastOpenedTicks": … },
    { "id": "…guid…", "name": "Work",    "dir": "D:\\Aperture Libraries\\Work",          "lastOpenedTicks": … }
  ],
  "activeId": "…guid…",
  "defaultSeeded": true
}
```

- **First run** registers the existing `%LOCALAPPDATA%\Aperture` as **"Default"** (zero migration) and sets
  `defaultSeeded`. After that a Default the user removed stays removed — unless no catalog at all is
  registered, because there must always be one library to open.
- **Tolerant load.** A missing file is an empty registry. Unreadable JSON (corrupt, or another program's
  file) is renamed to `catalogs.json.bad-<timestamp>` and never overwritten. Entries without an id, with an
  empty or relative `dir`, or duplicating another's id or folder are dropped; a dangling `activeId` is
  cleared. Hand edits may use comments, trailing commas, and any property-name casing; the file is
  written camelCase.
- **Durable save.** Write `catalogs.json.tmp`, flush to disk, rename over the file: a crash mid-write can't
  replace a good list with a torn one. `Save` reports failure.
- **Several Aperture windows.** Every change re-reads the file under a cross-process mutex and merges, so
  one window can't drop another's additions. Each window keeps its own active catalog in memory (another
  window switching doesn't change what this one shows); the file records the most recently switched-to
  catalog, which the next launch opens. A library a window has open stays listed even if another window
  removes it.
- **Remove vs. delete.** `Remove(id)` forgets a catalog; its files stay. `RemoveAndDeleteFiles(id)` forgets it,
  *then* deletes only Aperture's own files (`CatalogFileNames`: `aperture.db`, `thumbs.db`, their
  `-wal`/`-shm`/`-journal` files, `settings.json`) and the folder if that leaves it empty. Nothing else in the
  folder — the user's photos, other files, a nested catalog — is ever deleted. If the forget can't be saved,
  nothing is deleted; files that can't be deleted (in use) are reported, not swallowed.

### 2. Runtime switch — `Aperture.Core.Library.CatalogService`

`CatalogService` owns the registry and the one live `LibraryService`. Invariant: `Current` always
corresponds to `ActiveCatalog`.

- **Open the target first.** `SwitchToAsync` opens the target library *before* touching the current one. If
  that fails (unwritable folder, not a database, locked), the current library stays open and untouched and
  no event is raised. Only then: `PreparingToSwitch` → `await current.ShutdownAsync()` → swap → `SetActive`
  → `ActiveLibraryChanged`.
- **One switch at a time.** A semaphore serializes switch, create, open, remove and delete; a second request
  waits and then runs (the last request wins). Nothing can open a library and then lose it.
- **Events on the caller's context.** The shutdown await deliberately has no `ConfigureAwait(false)`, so a
  switch started on the UI thread raises both events on it — the host rebinds its window in
  `ActiveLibraryChanged`. (`DisposeAsync` keeps `ConfigureAwait(false)`: `App.OnExit` blocks the UI thread on it.)
- **Missing libraries aren't recreated.** Only the local Default may be created on the spot. Any other
  registered library must still have its `aperture.db`; a missing one (deleted folder, drive not connected,
  or a drive letter now pointing at another disk) is reported, not silently recreated empty.
- **Launch never bricks.** `Open` tries the active catalog, then Default, then the others most recently
  opened first, then re-registers Default; it reports which library failed and why in `OpenWarning`, which
  App shows once the window is up.
- **Teardown (`LibraryService.ShutdownAsync`).** Cancels in-flight indexes (tracked via `IndexRootAsync`,
  linked to a shutdown token), waits for them to stop, stops the watchers (no watcher can start afterwards),
  and disposes the database — the FunkyORM provider plus `SqliteConnection.ClearPool` for both files, so the
  folder can be deleted. A library that fails to open releases its files too. Once shut down,
  `GetOrCreateThumbnail` returns `null` instead of throwing, because queued tile loads and previews are
  `async void` and an exception would end the app.
- **Host (App + view model).** On `PreparingToSwitch`: `MainViewModel.PrepareForClose()` (stop new indexing,
  cancel running ones, drop the library subscription; queued work finishes quietly) and disable the window.
  On `ActiveLibraryChanged`: refresh the switcher, build a new view model, rebind `DataContext`, re-enable.
  `MainWindow` moves its view-model event handlers to the new view model and forgets scroll positions,
  which are keyed by per-library root ids.
- **Deferred: shared window geometry.** Window placement and folder-pane width are still stored per library
  (in each `settings.json`). Switching doesn't move the window — placement is restored only at launch — but
  the next launch uses the last-open library's saved placement. Moving those keys to a shared settings file
  is a follow-up, not part of this branch.

### 3. UI — the library switcher

At the top of the folder pane: the open library's name with a menu (`CatalogBarViewModel`, dialogs behind
`ICatalogDialogs` so the logic is testable):

- **Switch to ▸** the other libraries, most recently opened first.
- **New library…** asks for a name (not empty, usable as a folder name, not already taken), then a location
  (suggesting `%LOCALAPPDATA%\Aperture Libraries`), and creates the library in a new folder
  `<location>\<name>`. A folder that already exists must be empty.
- **Open library…** adds an existing library: the folder must already hold `aperture.db`. A folder already in
  the list just switches; a name clash gets a numbered name ("Photos (2)").
- **Rename this library…**, **Remove from list ▸**, **Delete library files ▸**. Remove and Delete list only the
  libraries you aren't in; the delete confirmation says exactly what is deleted and that photos are not.
- Commands are disabled while one runs. `Alt`/`F10` don't move focus to the menu (`IsMainMenu="False"`).
- With `APERTURE_DATA_DIR` set, Aperture uses only that folder, ignores the registry, and hides the switcher.

### 4. Edge cases (the retry/replay + failure lens)

| Case | Behaviour |
|---|---|
| Switch while indexing | The index is cancelled and awaited before the database closes; the indexer rolls back only its in-flight batch. |
| Target can't be opened | Opened first, so the current library is untouched; nothing is registered for a failed New/Open. |
| Registered library missing at switch | Refused with the folder path in the message; not recreated. |
| Active library missing at launch | Falls back (Default → most recent → re-register Default) with a message; the missing one stays listed. |
| Two quick switches | Serialized; the last request wins; nothing leaks. |
| Corrupt / foreign / hand-edited `catalogs.json` | Kept as `.bad-*`; sanitized on load; Aperture starts. |
| Two Aperture windows | Changes merge; neither drops the other's libraries. Two windows writing the *same* library's database is still possible and out of scope. |
| Deleting a library's files | Only Aperture's files; reported if some are in use; never if the forget couldn't be saved. |
| Legacy "Reel" migration | Unchanged — it targets the Default catalog's folder. |

## Work breakdown and tests

| # | Item | Tests (`tests/Aperture.Core.Tests`) |
|---|---|---|
| 1 | `CatalogRegistry` | `CatalogRegistryTests` (34) |
| 2 | `LibraryService.ShutdownAsync`, disposable `ApertureDatabase` | `LibraryServiceShutdownTests` (15) — the cancel/await tests hold an index mid-run with `BlockingProgress` |
| 3 | `CatalogService` + App/view-model rewiring | `CatalogServiceTests` (46) — includes events on a `SingleThreadContext` stand-in for the UI thread |
| 4 | Switcher UI | `CatalogBarViewModelTests` (20) — a real `CatalogService`, scripted dialogs |
| 5 | Manual verify | Create a second library, add a folder, switch back and forth (isolation); switch mid-index; open a missing folder; unplug a library's drive and relaunch; `APERTURE_DATA_DIR` still pins. **Not yet done.** |

## Revision 2 — what changed after review round 1 (2026-10-25)

Two independent reviews of revision 1 (`b60b370..418e5c1`) found 16 problems. The ones that changed this
design:

- **Delete removed the whole folder** the user picked, photos included (`Directory.Delete(dir, recursive)`).
  Now only Aperture's own files are deleted, New makes a dedicated folder, Open requires an existing library,
  and Remove/Delete act only on libraries you aren't in. Revision 1's suggested default location
  `%LOCALAPPDATA%\Aperture\Catalogs\<name>` nested new libraries inside Default's folder; it's gone.
- **Revision 1 tore down first and opened second**, so a target that couldn't open left the app on a closed
  library; it raised `ActiveLibraryChanged` on a thread-pool thread whenever an index was running; it allowed
  overlapping switches; and launch failed outright when the active library was missing. All four are fixed
  as described in §2.
- **The registry moved** from `%LOCALAPPDATA%\catalogs.json` (a generic name at the root of LocalAppData,
  which another program could own) to `%LOCALAPPDATA%\Aperture Libraries\catalogs.json`, and gained
  `defaultSeeded`, `.bad-*` quarantine, sanitizing, the cross-process merge, and a save that reports failure.
- **"Remove the open library by first switching away"** was replaced by acting only on libraries you aren't in.
- **Tests now have teeth:** revision 1's shutdown tests passed with the cancel or the await deleted; the new
  ones hold an index mid-run. Revision 1's claim that its tests pinned the indexer's rollback was not true and
  isn't repeated — that behaviour belongs to the pre-existing `Indexer`.

## Out of scope (future)

- Named saved views **within** a catalog (Option A) — complementary, can layer on later.
- Moving/copying items *between* catalogs.
- Shared window geometry across libraries (see §2).
- A catalog-picker launch screen (vs. always opening the last-active catalog).
