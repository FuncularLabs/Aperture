# Implementation plan — Multiple libraries (separate catalogs)

Status: **built on branch `feat/multiple-libraries`, unreleased** (revision 6, after review round 5) ·
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
- **Tolerant load.** A missing file is an empty registry. A file that isn't a readable library list —
  corrupt, not JSON, or JSON without a `catalogs` list (another program's file, a misspelled hand edit) — is
  renamed to `catalogs.json.bad-<timestamp>`, never overwritten, and reported (`QuarantinedFile`, shown at
  launch). Entries without an id, with an empty or relative `dir`, or duplicating another's id or folder are
  dropped; a dangling `activeId` is cleared. Hand edits may use comments, trailing commas, and any
  property-name casing; the file is written camelCase.
- **Durable, honest save.** Write `catalogs.json.tmp`, flush to disk, rename over the file (retrying for
  ~250 ms if something briefly has it open): a crash mid-write can't replace a good list with a torn one.
  A change that can't be saved is refused and undone — `Add` throws, `Rename`/`Remove` return `false` — so
  memory never holds a change that a later re-read would silently drop. Switching is the exception: this
  window still switches; only which library the next launch opens isn't recorded.
- **Never save over a list it couldn't read.** "No file" and "a file that can't be read right now" are
  different: if the file exists but stays locked or denied through ~250 ms of retries, `ReadFailed` is set
  and nothing is saved until a later read succeeds — changes are refused as for a read-only file. At launch
  that means Aperture opens Default for now and says so, rather than replacing the list with what little it
  has in memory.
- **Several Aperture windows.** Every read of the file and every change runs under a cross-process mutex
  (5 s timeout, then it goes ahead rather than hang; a lock an elevated window holds, which this window may
  not open, is waited for the same way), and every change re-reads and merges first, so one
  window can't drop another's additions. Each window keeps its own active catalog in memory (another window
  switching doesn't change what this one shows). The file records the library the user most recently
  switched to in *any* window, which the next launch opens. The library a window has open always stays
  listed: if another window removes it, it's listed again; if another window re-adds its folder under a new
  id, this window adopts that id.
- **Remove vs. delete.** `Remove(id)` forgets a catalog; its files stay. `RemoveAndDeleteFiles(id)` forgets it,
  *then* deletes only Aperture's own files (`CatalogFileNames`: `aperture.db`, `thumbs.db`, their
  `-wal`/`-shm`/`-journal` files, `settings.json`) and the folder if that leaves it empty. Nothing else in the
  folder — the user's photos, other files, a nested catalog — is ever deleted. If the forget can't be saved,
  nothing is deleted; files that can't be deleted (in use) are reported, not swallowed.
- **Open in any window.** Every open `LibraryService` holds its folder's lock file, `aperture.lock`, for as
  long as it's open (shared, so two windows may open one library). A file lock belongs to the physical file,
  so every path to the folder — a junction, a mapped drive, an 8.3 name — and every process, elevated or not,
  sees it, and Windows releases it if the window crashes. `LibraryService.OpenState(dir)` asks for the lock
  exclusively: a sharing violation means *open*; a lock file it isn't allowed to read means *unknown* (say, a
  folder copied from another PC); a missing or unreachable folder means *not open*. Remove refuses only
  *open* (it just forgets a list entry); Delete refuses *open* and *unknown*. Both check off the UI thread —
  the check touches the folder, and an offline network share can take tens of seconds to answer. As a
  backstop, deleting a library's files holds its lock exclusively for the whole delete (deleting nothing if
  any window has it open, and keeping any window from opening it meanwhile), and the lock file goes with the
  rest; a window that was waiting to open it (any library but the local Default) then re-checks for
  `aperture.db` and reports it gone instead of recreating it empty. A delete whose folder can't be reached deletes and forgets nothing, and says so; if the folder goes away
  mid-delete, the user is told nothing could be deleted rather than shown a success.

### 2. Runtime switch — `Aperture.Core.Library.CatalogService`

`CatalogService` owns the registry and the one live `LibraryService`. Invariant: `Current` always
corresponds to `ActiveCatalog`.

- **Open the target first.** `SwitchToAsync` opens the target library *before* touching the current one. If
  that fails (unwritable folder, not a database, locked), the current library stays open and untouched and
  no event is raised. Only then: `PreparingToSwitch` → `await current.ShutdownAsync()` → swap → `Activate`
  (which lists the target again if another window removed it) → `ActiveLibraryChanged`.
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
  App shows once the window is up. A fallback is for that launch only: the user's choice stays recorded, so
  a library on a drive that wasn't mounted yet at login opens again on its own once it's back (and the
  warning repeats on each launch until then).
- **A stale menu can't act on a gone library.** Remove, delete and rename re-read the list first and say so
  if another window already removed the library; switching to one re-lists it.
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
| Active library missing at launch | Falls back (Default → most recent → re-register Default) with a message, for that launch only; the missing one stays listed and is tried first next time. |
| Two quick switches | Serialized; the last request wins; nothing leaks. |
| Corrupt / foreign / hand-edited `catalogs.json` | Kept as `.bad-*` and reported at launch; sanitized on load; Aperture starts. |
| List file can't be saved (read-only, locked) | New/Open/Rename/Remove/Delete are refused with a message and change nothing; a switch still switches this window. |
| Two Aperture windows | Changes merge; neither drops the other's libraries; a library open in one can't be deleted from the other, nor removed while it's known to be open; a stale menu is told the library's already gone. Two windows writing the *same* library's database is still possible and out of scope. |
| Deleting a library's files | Only Aperture's files; reported if some are in use; never if the forget couldn't be saved; never while any window has it open, by any path. |
| The same folder registered twice through different paths (junction, mapped drive; an 8.3 short name is de-duplicated) | Two list entries for one folder. Safe while open: the lock file is shared, so neither entry can be removed or deleted while the folder is open through the other. Once neither is open, "Delete library files" on one leaves the other listed but pointing at a deleted library — switching to it then says so. |
| The list file can't be read at launch (locked by another program, or not accessible) | Opens Default for now, says so, and changes nothing on disk until the list can be read. If the first read fails but a second one succeeds, the user's own choice opens, not Default. A Default the user had removed can come back into the list if they keep working in it during such a launch (it's the library that window has open, and an open library is always listed). |
| A library on a drive or share that's gone | Remove from list works (without freezing the window); Delete says it can't reach the folder and changes nothing. |
| A library whose `aperture.lock` this user can't read | Remove from list works; Delete refuses, saying Aperture can't tell whether it's open. |
| Legacy "Reel" migration | Unchanged — it targets the Default catalog's folder. |

## Work breakdown and tests

| # | Item | Tests (`tests/Aperture.Core.Tests`) |
|---|---|---|
| 1 | `CatalogRegistry` | `CatalogRegistryTests` (70) |
| 2 | `LibraryService.ShutdownAsync`, disposable `ApertureDatabase`, `IsOpen` / lock file | `LibraryServiceShutdownTests` (28) — the cancel/await tests hold an index mid-run with `BlockingProgress`; the lock tests reach a folder through a real junction |
| 3 | `CatalogService` + App/view-model rewiring | `CatalogServiceTests` (83) — includes events on a `SingleThreadContext` stand-in for the UI thread |
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

## Revision 3 — what changed after review round 2 (2026-10-25)

Two independent reviews of the round-1 fix (`869145d`) confirmed 15 of the 16 round-1 findings fixed and
found new problems, nearly all in the round-1 fix's own cross-window code:

- **A change that couldn't be saved** stayed in memory and was silently dropped by the next re-read; a switch
  could then show a library that wasn't listed (`Current` ≠ `ActiveCatalog`). Unsaved changes are now refused
  and undone, the save retries a brief lock, the file is read under the lock too, and the active library is
  always listed (`Activate`).
- **Another window could delete the library this one had open**, its files freed once SQLite's idle pool let
  go. Now refused via the per-folder "open" marker.
- **A launch fallback became the saved choice**, so a drive that was late to mount at login lost its library
  for good. Fallbacks are now for that launch only.
- **Smaller:** a stale menu removing an already-removed library gave a false "couldn't save" message (now
  "already removed"); a set-aside list file was silent (now reported); valid JSON that isn't a list was
  overwritten (now set aside); Windows-reserved names (`NUL`, `CON`, `COM1`…) were accepted as library names.
- **Tests with teeth for branches that had none:** the cross-process lock (wait, timeout, abandoned), the
  Default-first fallback order, the last-resort Default, `PreparingToSwitch` while the old library is still
  open, a New whose folder can't be created, and a queued switch keeping the caller's thread.

Accepted residual: the cleanup after a New library that fails *halfway through* opening (folder created,
database partly written) has no deterministic test — there's no way to make SQLite fail mid-`Initialize`
without fault injection. It deletes only Aperture's own files, and only from a folder that was new or empty.

## Revision 4 — what changed after review round 3 (2026-10-25)

Round 3 found that revision 3's "library is open" marker — a named kernel event keyed on the folder's path
text — could be bypassed: the same folder reached by another path (a junction, a mapped drive, an 8.3 name)
got a different marker, so "Delete library files" on one list entry could delete the files of the library open
under the other (reproduced: tags and notes lost). It also failed outright against an elevated window, whose
event a normal window can't open. That was the second round in a row in which a fix introduced findings, so
the marker was **redesigned** rather than patched: an `aperture.lock` file held in the library's own folder,
which the OS ties to the physical file and which works across elevation (§1, "Open in any window"). The delete
holds that lock exclusively throughout, so data safety no longer depends on the "is it open?" check alone.

Round 3 also found that revision 3's save retry could overwrite the list with a stale copy: a read that gave up
on a locked file left this window's old copy standing, and the save's longer retry then wrote it — at launch,
replacing the whole list with Default. Reads now tell "missing" from "can't read right now", and nothing is
saved over a list that couldn't be read (§1, "Never save over a list it couldn't read").

Also: the launch-fallback warning now says how to stop it ("use Remove from list") when a library is gone for
good; Rename and Remove decide "already removed" from the same locked re-read that made the change (closing a
race instead of narrowing it); the last-resort Default is pinned as for-this-launch-only; and the reserved-name
list is Microsoft's complete one.

Accepted residual: an alias of an already-listed folder is registered as a second entry rather than
recognised as the same library. (Not only cosmetic: deleting the library through one entry leaves the other
pointing at a library that's gone — opening it then says so, and Remove from list clears it. No data is lost.)

## Revision 5 — what changed after review round 4 (2026-10-25)

Round 4 found no new High or Medium defects in the revision-4 design, only edge cases and test gaps:
"Remove from list" froze the window on an offline network library (the open check now runs off the UI
thread); a lock file the user can't read blocked Remove forever (now *unknown*: Remove allowed, Delete refused
honestly); a window waiting out a delete recreated the library empty (now re-checks under the lock); a registry
mutex made by an elevated window threw (now treated like a timeout); Rename checked duplicate names against a
stale list (now re-reads); Delete on an unreachable folder reported success (now refuses and keeps the entry);
the "can't read the list" warning blamed another program in every case (now neutral, with the file's path).
Tests now pin the "never save over an unread list" guard deterministically (it sits in `Save` itself, so every
path that saves is covered; an internal hook releases the lock in the exact window for Add, Remove and EnsureDefault — and, since revision 6,
Rename and switching too), a quarantine move that fails, an
access-denied list, the read retry, the *unknown* state, and the lock being taken before any database file is
opened. And a launch whose first read of the list failed but whose second succeeded now opens the user's own
library rather than Default.

## Revision 6 — what changed after review round 5 (2026-10-25)

Round 5 found no High or Medium defects. Fixed: revision 5 skipped a registry lock an elevated window held
instead of waiting for it (the lock exists only while that window is mid-change, so skipping it invited a lost
update) — it's now waited for like any held lock, up to the timeout; a folder that went away *during* a delete
was reported as a success — the user is now told nothing could be deleted; Switch to, New library and Open
library now open the target off the UI thread too, like Remove and Delete; and several docs that overclaimed
after the three-state open check were corrected. New tests pin Switch to, New, Open, Remove and Delete touching the library folder off the caller's thread (an internal hook records the thread), the "nothing to name: report the folder" fallback, the bounded wait for an unopenable lock, the
stray-lock clean-up, "never creates the folder", and "lists the lock file only if there is one"; the brief-lock
tests release from a dedicated thread so a busy thread pool can't fail them.

Round 5's second review (of revision 5) also found that revision 5's "can't reach the folder" check read this
window's copy of the list before re-reading the file, so deleting a library another window had already deleted
said "can't reach" instead of "already removed"; it now re-reads first. Its probes became tests: a switch to, or
an Open of, a library deleted while this window waited for its lock fails and recreates nothing; Rename and
switching never save over a list that couldn't be read; a share that isn't there reads as not open; and the
elevated-lock tests check that the lock really can't be opened, so they can't pass vacuously.

Accepted residual: at launch, opening the last-used library still touches its folder on the UI thread (before the
window appears), so a library on an unreachable share can delay startup until Windows gives up on it (about 40 seconds
measured), then Aperture opens Default for that launch.

## Out of scope (future)

- A single-instance guard (a second launch focuses the first window). It would make most of the cross-window
  handling above unnecessary; worth considering for a later release.

- Named saved views **within** a catalog (Option A) — complementary, can layer on later.
- Moving/copying items *between* catalogs.
- Shared window geometry across libraries (see §2).
- A catalog-picker launch screen (vs. always opening the last-active catalog).
