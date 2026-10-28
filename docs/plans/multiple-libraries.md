# Implementation plan — Multiple libraries (separate catalogs)

Status: **built on branch `feat/multiple-libraries`, unreleased** (revision 36, after review round 36) ·
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
  mid-delete, the library has already been forgotten but not all of its files were deleted, and the user is told
  exactly that — and how to finish once the drive is back (add it again with Open library…, switch to another
  library, then delete it). "Can be reached" means Aperture could list the folder, never
  just that the path exists: a junction to a drive that's gone still "exists".

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
  On `ActiveLibraryChanged`: refresh the library list, build a new view model, rebind `DataContext`, re-enable.
  `MainWindow` moves its view-model event handlers to the new view model and forgets scroll positions,
  which are keyed by per-library root ids.
- **Deferred: shared window geometry.** Window placement and folder-pane width are still stored per library
  (in each `settings.json`). Switching doesn't move the window — placement is restored only at launch — but
  the next launch uses the last-open library's saved placement. Moving those keys to a shared settings file
  is a follow-up, not part of this branch.

### 3. UI — libraries in the folder pane (revision 15)

The folder pane lists the libraries under a **LIBRARIES** header (`CatalogBarViewModel`, dialogs behind
`ICatalogDialogs` so the logic is testable), above **FOLDERS**, which lists the open library's folders:

- The header collapses and expands the list (for this session). Its **+** menu has **New library…** and
  **Open library…**.
- Every library is listed by name, with its item count (the open one's live; the others' as they were when
  last open — revision 21). The open one is marked; clicking it shows everything in it (what "Everything" did). Clicking another library switches to it.
- Right-click a library: **Rename…**, **Remove from list**, **Delete library files…**. Remove and Delete are
  disabled on the open library; the delete confirmation says exactly what is deleted and that photos are not.
- **New library…** asks for a name (not empty, usable as a folder name, not already taken), then for the
  folder of photos and videos to put in it, then whether to include that folder's subfolders (Yes is the
  default). It keeps the new library's own files in `%LOCALAPPDATA%\Aperture Libraries\<name>` — or
  `<name> (2)`, and so on, if that folder is taken — switches to it, adds the folder, and starts indexing it.
- **Open library…** adds an existing library: the folder must already hold `aperture.db`. A folder already in
  the list just switches; a name clash gets a numbered name ("Photos (2)").
- Commands are disabled while one runs.
- With `APERTURE_DATA_DIR` set, Aperture uses only that folder, ignores the registry, and the pane shows
  "Everything" instead of a library list.

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
| Deleting a library's files | Only Aperture's files; reported if some are in use; never if the forget couldn't be saved; never while any window has it open, by any path. If the folder goes out of reach mid-delete, the library is forgotten and the user is told not all of its files were deleted, and how to finish (the database is deleted last, so the folder still has it and can be added back). A folder this user may not list is refused with its own message (nothing deleted, nothing forgotten; Remove from list still works). |
| Aperture closed while New library is creating | The folder left behind holds only Aperture's lock (or a new, empty database). New library with the same name uses a lock-only folder again (a lock another window holds still counts as taken); one with a database is passed over for `<name> (2)` (revision 15), and can be added with Open library…. |
| The same folder registered twice through different paths (junction, mapped drive; an 8.3 short name is de-duplicated) | Two list entries for one folder. Safe while open: the lock file is shared, so neither entry can be removed or deleted while the folder is open through the other. Once neither is open, "Delete library files" on one leaves the other listed but pointing at a deleted library — switching to it then says so. |
| The list file can't be read at launch (locked by another program, or not accessible) | Opens Default for now, says so, and changes nothing on disk until the list can be read. If the first read fails but a second one succeeds, the user's own choice opens, not Default. A Default the user had removed can come back into the list if they keep working in it during such a launch (it's the library that window has open, and an open library is always listed). |
| A library on a drive or share that's gone | Remove from list works (without freezing the window); Delete says it can't reach the folder and changes nothing — also when the library is reached through a junction or link whose drive is gone. |
| New library whose folder is a listed library's, or already holds files | Passed over for the next free folder, `<name> (2)` and so on (revision 15; before that, when the user chose the location, refused). |
| A library whose `aperture.lock` this user can't read | Remove from list works; Delete refuses, saying Aperture can't tell whether it's open. |
| Legacy "Reel" migration | Unchanged — it targets the Default catalog's folder. |

## Work breakdown and tests

| # | Item | Tests (`tests/Aperture.Core.Tests`) |
|---|---|---|
| 1 | `CatalogRegistry` | `CatalogRegistryTests` (82) |
| 2 | `LibraryService.ShutdownAsync`, disposable `ApertureDatabase`, `IsOpen` / lock file | `LibraryServiceShutdownTests` (28) — the cancel/await tests hold an index mid-run with `BlockingProgress`; the lock tests reach a folder through a real junction |
| 3 | `CatalogService` + App/view-model rewiring | `CatalogServiceTests` (92) — includes events on a `SingleThreadContext` stand-in for the UI thread |
| 4 | Switcher UI | `CatalogBarViewModelTests` (21) — a real `CatalogService`, scripted dialogs |
| 4b | One window (revision 11) | `SingleInstanceTests` (13), `RelaunchTests` (16) — real kernel objects and real child processes |
| 4c | Paul's first trial (revision 15) | `CatalogBarViewModelTests` (now 25), `CatalogServiceTests` (free folder), `HiddenFoldersTests` (14), `FolderTreeTests` (9), `FolderNodeVmTests` (4), `FolderAdditionsTests` (4), `WindowTitleConverterTests` (4), `MainWindowTemplateTests` (5), `AddFolderDialogTests` (2), `TileLayoutTests` (1), `BackgroundRefreshTests` (5) — the WPF ones on an STA thread; switch timing in a scratch harness on a copy of a real library (not in CI) |
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

Accepted residual (see revision 7 for its full extent): at launch, opening the last-used library still touches its
folder on the UI thread (before the window appears), so a library on an unreachable share can delay startup until
Windows gives up on it.

## Revision 7 — what changed after review round 6 (2026-10-25)

Round 6 found no High or Medium defects. Fixed:

- **The "folder lost mid-delete" message.** Revision 6 said the user was told nothing could be deleted; the
  message actually said "some of its files couldn't be deleted (another program may be using them)" and named
  the folder as a file. A delete now reports that case as its own outcome, and the user is told the library was
  removed from the list but none of its files were deleted, and how to finish. Relatedly, a delete that can't
  take the lock in a folder holding none of Aperture's files no longer reports the folder as "left".
- **Folder work on the UI thread during a switch.** Closing the library a switch leaves, and the clean-up after
  a New or Open whose list couldn't be saved, now run off the UI thread too. (Not all of it: see revision 8.)
- **An interrupted New.** Closing Aperture while New library was creating could leave a folder holding only
  `aperture.lock`, which then blocked both New and Open with that folder. New now treats an unheld lone lock as
  a leftover and uses the folder.
- **Tests with no teeth.** The elevated-lock test now also proves the change ran *with* the lock and went on as
  soon as the other change ended (not at the timeout); New and Open now pin their events to the caller's thread
  like Switch; the "deleted just after the check" tests no longer depend on a 20 ms sleep.
- Smaller: the lock wait measures elapsed time with a stopwatch, not the wall clock; a comment and a test
  helper's name now say what they do.

Not pinned, by design: the delete's second "already removed" check (another window removing the library between
the re-read and the forget) is reachable only through that race; it's kept as a backstop and checked by reading.

Accepted residual (**accepted by Paul, 2026-10-26**): at launch the last-used library is still opened on the UI thread,
before the window appears. On an unreachable share each unreachable host costs about 40 seconds (Windows
remembers a host it gave up on, so two libraries on the same host cost about the same). Aperture then falls back
for that launch: to Default if it's listed; if the user removed Default, it tries the other listed libraries
first — opening the first reachable one, or after the last re-registering Default — so two libraries on two
unreachable hosts took about 84 seconds. The alternative is to give up on a library after a few seconds, at the
risk of skipping a slow NAS that is only waking up.

## Revision 8 — what changed after review round 7 (2026-10-25)

Round 7 found one **Medium** defect, introduced by revision 7: a library reached through a junction (or link) to
a drive that's gone was forgotten by Delete library files with no message, its tags and notes left on the drive.
`Directory.Exists` says yes for such a junction, and revision 7 had removed the fallback that used to catch it.
This was the third round running whose fix caused the next round's finding, so the reachability checks were
redesigned rather than patched again:

- **Reachable means touched.** Before deleting, the folder is listed (not just checked for existence); a
  folder that can't be listed is "can't reach": nothing deleted, nothing forgotten. During the delete, the lock
  and every file are classified by what actually happened: in use or no permission is a file left over;
  anything else (not found, a path or network error) means the folder went out of reach, and the user is told
  not all of its files could be deleted — including when the drive goes after the delete took its lock, which
  revision 7 still reported as a clean delete.
- **The tests use the real thing:** a library behind a junction whose drive is moved away (the same
  `DirectoryNotFoundException` a real unplug gives), just before the delete, just before its lock, and once its
  lock is held.

Also fixed:

- **New library in a listed library's folder** (one emptied by hand) silently reused that library's entry and
  dropped the new name; it's now refused, naming the library.
- **The off-thread tests watched the code around the work, not the work.** Closing a library now reports to the
  test hook from inside the close itself, so moving the close back onto the UI thread fails a test.
- **The "how to finish" message** now says to switch to another library before deleting (a library can't be
  deleted while it's the open one).
- **Tests:** the elevated-lock test no longer crashes the test run when the wait it guards is skipped; New and
  Open queued behind another switch still notify on the caller's thread.

Accepted residuals:

- Switching still reads the *new* library's database on the UI thread when the window rebuilds its view model
  (in the exempt, untested App shell; unchanged by this feature's fixes).
- Closing the window during the clean-up after a New whose list couldn't be saved can leave some of Aperture's
  files in the folder. (Since revision 9 the database goes last, so Open library… can add such a folder back.)
  Widening "a leftover" beyond a lone lock would risk deleting files that aren't Aperture's (Windows' own
  `Thumbs.db` shares the name of Aperture's thumbnail cache).
- The delete's second "already removed" check stays a backstop checked by reading (revision 7).

## Revision 9 — what changed after review round 8 (2026-10-25)

Round 8 verified the round-7 Medium fixed (both kinds of unplug, at the registry, service and view-model levels)
and found no High or Medium defects. Fixed:

- **The "how to finish" advice couldn't always be followed** (found by both reviews). The delete removed the
  database first, so a drive lost partway left a folder Open library… refuses. The database now goes last.
- **A folder this user may not list counted as reachable** (both reviews), against revision 8's own rule; a
  delete there could forget the library while its files stayed. It now counts as out of reach: nothing deleted,
  nothing forgotten.
- **Errors that don't mean the folder is gone** (a write-protected disk) were reported as "lost access…
  reconnect the drive". Any error other than "in use" or "no permission" is now checked against the folder:
  still reachable means that file is reported as left. The left-over message no longer names only one cause.
- **New checked its name before re-reading the list**, so two windows could each create a library with the same
  name; it now re-reads first.
- A folder that happens to have one of Aperture's file names is left alone rather than reported as left over.
- Tests: deletion order, a drive lost after the first file, an unlistable folder, a read-only file, a disk error
  at the lock and at a file (raised with Windows' real ERROR_WRITE_PROTECT code from the delete's test seams),
  New against another window's name and folder, and New's clean-up off the UI thread.

Recorded, not changed:

- If the host's PreparingToSwitch handler throws, the library just opened is closed on the caller's thread (a bug
  path in the host, untested).
- The "lock violation" half of the in-use check can't occur for these calls; it mirrors LibraryService's.
- Unverified: if a removed volume answered with "file not found" rather than "path not found", each delete
  would succeed silently and the result would read as a clean delete.

## Revision 10 — what changed after review round 9 (2026-10-25)

Round 9 found no High or Medium defects; the disposition review found nothing above Nit and followed the "how
to finish" advice end to end. Fixed:

- **A library whose folder this user may not list** still opens and works, but Delete said "can't reach… if
  it's gone for good, use Remove from list". It now says Aperture isn't allowed to see what's in the folder,
  nothing was deleted, and Remove from list still works.
- **Tests:** the drive-lost-partway test now loses the drive just before the database, after the other files are
  really gone, and a service-level test follows the message's advice to the end (reconnect, Open library…,
  switch away, Delete); the realistic unplug — a junction left pointing at nothing — is pinned mid-delete; the
  left-over message's wording is pinned (both its "the files below" and its causes), and so is which of the two
  "nothing deleted" messages a folder gets.
- **Wording:** "can be reached" means listed (not "or a file opened"); "can always be added back" becomes "still
  has its database" (an earlier crash's journal deleted before the database could, in principle, leave one that
  won't open); the left-over message says "the files below" rather than "some of its files"; the delete's doc
  comment names every case.

Accepted residuals:

- **Two windows naming two libraries the same at the same moment.** Revision 9's re-read narrowed this race; it
  isn't closed, because the name is checked before the new library is built, not inside the list's locked change.
  The worst case is two libraries with one name, in different folders — rename either. Open library… (its name
  is made unique against the list read before it opens) and Rename have the same shape. **Accepted by Paul, 2026-10-26.**
- On a share that drops mid-delete, a failing file can now cost a few network timeouts (the folder is listed to
  classify the error); it runs off the UI thread, with the switcher's commands disabled meanwhile.

## Revision 11 — one Aperture window (Paul, 2026-10-26)

Round 10 found only Nits (its regression review was CLEAN); round 11 was CLEAN at 69633b8. Paul then ruled on
the open items: the launch delay on an unreachable share is **kept**; the same-name race is **accepted**;
`WpfCatalogDialogs.cs` and `TextPromptDialog.xaml.cs` are **exempt** from the coverage floor, like the other
WPF shells; and **Aperture runs as a single window**.

Design of the single window:

- At launch, before anything else, Aperture claims a named event, `Local\Aperture.Window.<hash>`, per Windows
  session. The hash is of the library list's path — or, with `APERTURE_DATA_DIR`, of the pinned folder, so a
  pinned (test) copy runs beside the user's own Aperture, one window per pinned folder.
- The first launch owns the event until its window starts closing (revision 12). Windows removes it when that
  process exits or crashes, so
  nothing stale is ever left to block a launch.
- A later launch finds the event, signals it, and exits without opening a window; the running one brings its
  window to the front (restored if minimized). The later launch first lets the running one take the
  foreground, which Windows otherwise refuses a background process.
- If the running one can't be signalled (it runs elevated and this launch doesn't), the later launch says
  Aperture is already running and exits — it still never opens a second window.
- A relaunch (an in-place update, a portable update, the installer's "move") starts the new copy with
  `--after <pid>`; the new copy waits, up to 30 seconds, for that process to exit before claiming, so it doesn't
  hand the window to the copy that is closing.
- Two launches at the same moment: exactly one claims the event (Windows decides); the other signals it.

The cross-window protections from earlier revisions stay: they still matter for a pinned copy, the same user in
another Windows session, and a library on a shared drive that another PC has open. (Released versions before
this one have neither the lock file nor the library list, so they can't see each other either way.)

| AC | Behaviour | Tests (`SingleInstanceTests`, `RelaunchTests`) |
|---|---|---|
| SW1 | The first launch is the instance; a later one isn't | `TheFirstLaunch_IsTheInstance_ALaterOneIsNot` |
| SW2 | A later launch brings the running window forward, every time | `ALaterLaunch_AsksTheRunningOneToComeForward`, `EveryLaterLaunch_AsksAgain` |
| SW3 | Once the running one has closed, the next launch is the instance | `OnceTheRunningOneHasClosed_TheNextLaunchIsTheInstance` |
| SW4 | A running one this launch can't signal is reported, never joined by a second window | `ARunningOneThisLaunchMayNotSignal_IsReported` |
| SW5 | Two launches at once: exactly one instance | `ManyLaunchesAtOnce_OnlyOneIsTheInstance` |
| SW6 | One name per place; different places, different names | `NameFor_IsOnePerPlace` |
| SW7 | A relaunch waits for the copy it replaces | `ALaunchStartedToReplaceAnother_ClaimsOnlyOnceThatOneHasExited`, `AnOrdinaryLaunch_ClaimsWithoutWaiting`, `ArgsAfter_…`, `PreviousProcessId_…`, `WaitForExit_…`, `RestartStartInfo_StartsTheNewCopyAfterThisOne` |
| SW8 | An activation handler that throws doesn't stop the next activation | `AnActivationThatThrows_DoesntStopTheNext` |
| SW9 | Closing the instance stops it listening; closing twice is harmless | `Dispose_StopsListening_AndCanBeRepeated`, `Dispose_FromInsideTheActivation_DoesntHang` |
| SW10 | A launch while Aperture is closing (or failing to start) opens a window (see the accepted millisecond race below) | App wiring: verified by reading and by round 13's WPF probe (the real Core classes wired as the App wires them) |

The wiring (`App.xaml.cs`, `MainWindow.xaml.cs`, and the install-move in `MainViewModel.cs`) is in the exempt
shells. The two update relaunches in `UpdateService` start the new copy with `RestartStartInfo`, which is tested;
that they call it is checked by reading — those methods replace the running exe, or put a new one beside it, and
start it, which a test can't let them do.

Live check (Paul) — a smoke test of the one-window behaviour; it can't test SW10's close ordering, because a close
is over in well under a second even mid-index (the indexer stops between files), so SW10 rests on reading and on
round 13's probe. First close every other Aperture, including an installed older version (it doesn't take part
in the guard, so it would still show its own window). Then: launch twice — one window; minimize it and launch
again — it comes back (maximized, if it was); close it and launch again — it opens.

### Revision 12 — what changed after review round 12

Round 12 (two reviews) found one Medium: the claim was given up only at the very end of exit — after the settings
save and the library's teardown — so a launch while Aperture was closing (or while its startup-error box was up)
did nothing at all, and an update's new copy could time out waiting and leave no Aperture running. The window is
now given up as soon as it starts closing (before anything is saved), again first thing on exit, and before the
startup-error box; a launch in the meantime opens normally while the old copy finishes closing, as before this
feature. Also fixed: a test that relied on the System process being unwatchable (an elevated CI runner can watch
it) now raises the real access-denied error through a seam; "wait for `--after`, then claim" is one Core method
tested with a live child process; the activation tests count activations exactly (a reset-mode bug would bring
the window forward over and over, a wake-on-close bug once more as it closes); messages no longer speak of "another Aperture window"; the
"already running" message no longer assumes the other copy is elevated.

Accepted (one window, as ruled):

- A launch while Aperture is running but hung brings nothing forward until that copy is closed.
- A launch that asks in the milliseconds just before a close starts gets nothing (the copy it asked is going away):
  launch again.
- "Run as administrator" while Aperture runs normally brings the normal window forward instead of starting an
  elevated one; a newer exe started by hand while this one runs brings the running window forward.
- **From when an update's old copy starts closing until its new copy claims** — the old copy's close (measured:
  well under a second, even mid-index) plus the new copy's start — a launch by hand becomes the window, and the
  new copy exits. The update is on disk either way. After
  an in-place update the hand launch runs the new version (harmless). After the installer's move the downloaded
  exe isn't deleted (delete it by hand). After a portable update a shortcut to the old exe runs the old version
  until the new exe is started. *(Revision 13: wider than "an instant" since the window is given up at close.
  Accepted by Paul, 2026-10-26.)*
- Pre-existing, from the two-window days: `settings.json` is written in place, and a read that fails counts as no
  settings. A launch whose settings load overlapped the closing copy's save (only on a slow library drive) would
  start with default settings.
- `UpdateService`'s update-collision messages still mention other windows: still possible with a pinned copy or
  another session, so left as they are.

### Revision 13 — what changed after review round 13

Round 13 verified the round-12 Medium fixed (a WPF probe drove the real Core classes as the App wires them: the
window is given up before its own Closing handler saves, on `Shutdown()` too, and before the startup-error box;
launches during each close opened). Fixed: the relaunch test now has the closing copy hold the window, so
"claim first, then wait" fails it; the live check is described as the smoke test it is (revision 14: it can't catch
a slow close, because measured closes are fast); comments and this plan no longer say the claim lasts the whole
run; the update-gap residual above states its real extent.

### Revision 14 — what changed after review round 14

Round 14 found the code and tests sound, but the live check could not fail, as worded. It is now described as a smoke
test, and SW10 rests on reading and on round 13's probe. The millisecond race (a launch that asks just before a
close starts) is recorded with the accepted residuals above. Round 15 was CLEAN at 3d13e9d.

## Revision 15 — Paul's first trial (2026-10-26)

Paul tried the build from 3d13e9d. He accepted the update-gap residual, and reported six things:

1. **Bug:** a new library "with one directory" didn't show the directory or index it. The library was created
   at `D:\func\Funcular` with no folders. New library's second question, "Choose where to create the library
   folder", reads as "choose the folder for this library", so the folder of photos was taken as the place for
   Aperture's own files.
2. The dropdown at the top of the pane is unintuitive: libraries belong under the LIBRARIES header, which should
   collapse, with the selected library's folders shown below.
3. **Bug:** switching back to Default is unexpectedly slow.
4. Adding a folder for the first time should ask whether to include subfolders (Yes by default).
5. The title bar should show the library's name before the folder's.
6. Subfolders should have show/hide checkboxes, like the top-level folders.

**Item 3, measured.** A harness drove the real `MainWindow` and view model, switching as App does, against a copy
of Paul's Default library (45,914 items, its 2 GB thumbnail cache). Launch to all visible thumbnails took 4.9 s;
switching back took 7 s and grew with every switch, and switching to an *empty* library took 3.3 s. The cause:
each tile's template bound its size four times through the window's view model (`RelativeSource
AncestorType=Window`) and carried its own context menu with a dozen bindings through the view-model proxy. A
library switch gives the window a new view model, so WPF re-evaluated every one of those bindings — about 1,000
tiles per load, including tiles already discarded but not yet collected (19,000–30,000 bindings after a few
switches). The ancestor lookups also made each tile's size unknown at creation, so the grid built roughly 1,000
tiles to show 153. With the size inherited from the grid (`TileLayout.Size`) and one tile menu on the grid:
launch 1.2 s, switching back 0.8 s and flat, switching away 0.06 s, managed heap 85 MB instead of 380 MB.

Design:

- **F1** — New library asks for the folder to put in the library, not where to keep Aperture's files: name →
  folder → "Include subfolders?" (Yes by default) → the library's own files go in `%LOCALAPPDATA%\Aperture
  Libraries\<name>`, or the next free `<name> (n)` → switch → the folder is added and indexed at once, as Add
  folder does — once (revision 16: the startup reconcile covers only the folders the library opened with).
  Cancelling at any step creates nothing. (Choosing where a new library's files go is gone; Open
  library… still adds a library from anywhere.)
- **F2** — libraries are listed in the pane (§3). The collapsed state lasts the session. Only the open library
  shows a count: counting another would mean opening it.
- **F3** — tiles take their size from `TileLayout.Size` (an inherited value set on the grid) and the grid has one
  tile menu, which `OnGridContextMenuOpening` points at the tile it opens on (the one under the pointer, or the
  focused one for the menu key or Shift+F10; none on empty canvas or a section header).
- **F4** — every way of adding a folder asks once about subfolders, Yes by default: Add folder, dropping
  folders on the window (one question per drop), New library, and the first-run panel (a checkbox, ticked).
- **F5** — title: `Aperture Image Browser vX — <library> — <folder>`, or `— <library>` at the top of a library;
  with `APERTURE_DATA_DIR` there's no library name.
- **F6** — a subfolder's checkbox hides it, exactly as a top-level folder's checkbox does: its photos and
  videos, and those of every folder inside it, leave Everything, the grid and the counts above it (a ticked
  root's too — an unticked root shows nothing, so its count stays everything in it); the folder stays listed,
  unticked, with its own count, so it can be shown again. Hidden folders are kept per library in a
  new `hidden_folders` table (created on open, so an existing library just gets an empty one; a version
  without it ignores it). They are still indexed. A hidden folder whose items have all gone (renamed or deleted
  on disk) isn't listed.

| AC | Behaviour | Tests | Mutation each must kill |
|---|---|---|---|
| F1 | New library: name → folder → subfolders → created under the libraries folder, switched to, folder handed over to be added and indexed — once; cancel at any step creates nothing; a failed create hands nothing over | `CatalogBarViewModelTests.NewLibrary_AsksForANameThenAFolderThenSubfolders_…`, `…_WithoutSubfolders_…`, `…_CancelledAtTheFolderOrTheSubfolderQuestion_CreatesNothing`, `…_ThatFails_HandsOverNothing`; `CatalogServiceTests.CreateLibrary_PassesOverATakenFolder_ForTheNextFreeOne`; `BackgroundRefreshTests.AFolderAddedBeforeTheStartupReconcile_IsIndexedOnce`, `…AFolderRemovedBeforeTheStartupReconcile_IsNotIndexedOrWatched`, `…AFolderAddedAfterTheLibraryHasClosed_IsIgnored`, `…AddingFoldersToAClosedLibrary_AsksNothingAndAddsNothing`, `…ALibrarySwitchWhileTheSubfolderQuestionIsOpen_AddsNothing` (a real view model, STA) | ask for the location again; raise the hand-over before the switch; drop the free-folder search; let the reconcile read the folders after its delay; let it index a folder removed meanwhile; add to a closed library, ask a closed library's question, or add after a switch during the question |
| F2 | Every library listed by name, the open one marked; a switch moves the mark; Remove/Delete disabled on the open one; Rename works on any library; the list collapses | `CatalogBarViewModelTests.Libraries_ListEveryLibraryByName_MarkingTheOpenOne`, `…_FollowASwitch`, `RemoveAndDelete_AreDisabledForTheOpenLibrary`, `Rename_RenamesTheLibraryItIsGiven`, `IsExpanded_StartsOpen_AndToggles` | list only the others; enable Remove on the open one; rename the open one regardless |
| F3 | No tile binding goes through the window or the view-model proxy — written in the tile's XAML (in the usual spellings), or in the objects the tile builds, its container's style, and the keyed styles, templates and resources it uses (implicit styles from outside the tile, DynamicResource references and InputBindings aren't checked; the tile uses none of them); the grid has one tile menu; the size set on the grid reaches every tile | `MainWindowTemplateTests.TheTileTemplate_BindsNothingThroughTheWindowOrTheProxy` (the objects the real tile builds and reaches), `…TheBindingWalk_FindsABindingInEveryPlaceItWalks` (the walk's completeness: a window binding planted in each of the 30 places it looks), `…TheTileTemplatesXaml_WritesNoBindingThroughTheWindowOrTheProxy` (what is written in the tile's XAML, wherever it sits), `…TheGridHasOneTileMenu_ActingOnItsDataContext` (real `MainWindow`, STA); `TileLayoutTests.ASizeSetOnTheGrid_ReachesEveryTileInsideIt` | restore one `AncestorType=Window` size binding; put the menu back in the tile; bind the image brush, an effect, a gradient stop, or a template, trigger, resource or based-on style's binding through the proxy or the window; write such a binding where the walk doesn't go (the text check alone); drop any part of the walk that finds bindings (the completeness test); drop `Inherits` |
| F4 | One question per add, for new folders only; Yes by default; cancel adds nothing; folders already added or gone are skipped without asking | `FolderAdditionsTests` (4), `AddFolderDialogTests` (2) | ask per folder; ask when nothing is new; default to No |
| F5 | Title: product — library — folder; product — library at a library's top; no library name when pinned | `WindowTitleConverterTests` (4); `MainWindowTemplateTests.TheTitle_IsTheProductThenTheLibraryThenTheFolder` (the window's own bindings, in order) | drop the library; swap the order — in the converter and in the window's bindings |
| F6 | Hiding a subfolder takes it and everything inside it out of the union and back on showing; persisted; counts reported; root removal forgets them; "Trip" hidden leaves "Trip 2"; case and separators don't matter; the root can't be hidden this way; an existing database gains the table; the tree lists a hidden folder unticked with its count and no children, keeps a folder that only leads to one, and drops one with no items; nested hidden folders each count everything inside them; a folder is hidden in its own root only, and the tree lists only its own root's folders; a hidden folder in an unticked root counts nothing; showing ignores case beyond ASCII; the node reports a tick or untick once | `HiddenFoldersTests` (14), `FolderTreeTests` (9), `FolderNodeVmTests` (4) | `StartsWith(dir)` without the separator; filter only the folder itself, not inside it; forget to persist; list the hidden folder's children; count a nested hidden folder twice, or an item for one hidden folder only; ignore the hidden folder's root, or the row's; match in SQL (ASCII-only NOCASE); report a non-change |

`MainViewModel.cs`, `App.xaml.cs` and `MainWindow.xaml.cs` remain exempt from the coverage floor (ruling of
2026-10-25): the wiring there — the checkbox calling `SetFolderShown`, App handing New library's folder to the
new view model, the drop and first-run questions, the tile menu finding its tile — is checked by reading and by
Paul's live check (the reconcile's snapshot is tested, above).

Live check (Paul): create a library with New library… and one folder — it's listed under LIBRARIES with its
count, the folder appears under FOLDERS and its thumbnails stream in, and Pause on the folder stops it; click
between libraries several times (each switch about as quick as the first); click the open library — it shows
everything in it; collapse LIBRARIES; right-click a library and rename it; right-click the open library (Remove
and Delete are greyed out); the title shows the library's name; untick a subfolder — its photos leave Everything
— and tick it again; drop a folder on the window — it asks about subfolders, and Explorer isn't stuck while it
does; right-click a tile, and press the menu key (or Shift+F10) on a selected tile — the tile menu acts on that
tile ("Open with" lists its apps); right-click empty canvas or a date header — no menu. (The first-run panel's
"Include subfolders" box only shows for a brand-new Default library or an empty `APERTURE_DATA_DIR` folder;
check it only if that's handy.)

### Revision 16 — what changed after review round 16

Two non-author reviews of 3d13e9d..091f0b6 — behaviour, regressions and leaks (A); proof and prose (B). Both were
NOT CLEAN on one Medium; every finding A raised, B raised too (×2).

| # | Finding | Severity | Blame | Fix, and the test that now fails without it |
|---|---|---|---|---|
| A1 / B F-1 ×2 | New library's folder was indexed twice at once: the startup reconcile (2.5 s after the switch) took the folder New library had just handed over, while that folder's own index was running. The second run overwrote the first's pause/cancel handle, so Pause stopped only one run and the folder then read "paused" while indexing on | Medium | AC-GAP | F1 now says "once". The reconcile covers the folders the library opened with, read before its delay. `BackgroundRefreshTests.AFolderAddedBeforeTheStartupReconcile_IsIndexedOnce` — a real view model, driven as App drives it (fails with 2 starts if the reconcile reads the folders after its delay) |
| A2 / B F-2 ×2 | The title's order and library name live in the window's MultiBinding, which no test read | Low | TEST-GAP | `MainWindowTemplateTests.TheTitle_IsTheProductThenTheLibraryThenTheFolder` (swapping or renaming a binding in the XAML fails it) |
| B F-3 | The tile-template test walked only elements' own plain bindings: not brushes, effects, styles, triggers, MultiBindings or templates | Low | TEST-GAP | The walk covers them all, and asserts it reaches the image brush (binding the brush, or an effect, through the proxy or the window fails it) |
| A3 / B F-4 ×2 | The hidden-folder filter and counts were never tested with two roots, or with nested hidden folders together | Low | TEST-GAP | `HiddenFoldersTests.HidingAFolder_HidesItInItsOwnRootOnly`, `…NestedHiddenFolders_EachCountEverythingInsideThem` |
| B F-5 | `FolderTree.Subfolders` was never given another root's rows | Low | TEST-GAP | `FolderTreeTests.AnotherFoldersRows_AreNotListedHere` |
| A5 / B F-6 ×2 | "A root's count leaves out its hidden subfolders" was false for an unticked root | Low | AC-GAP | F6 and the comment now say "a ticked root's"; an unticked root shows nothing, so it still counts everything in it. `HiddenFoldersTests.AHiddenFolderInAnUntickedFolder_CountsNothing` pins what the count rests on |
| A4 / B F-7 ×2 | Dropping folders showed the subfolder question inside the drop, so Explorer waited until it was answered | Low | OTHER (UI work inside an OLE callback) | The drop posts the add, which asks once the drop has returned. In the exempt `MainWindow.xaml.cs`: checked by reading, and in the live check |
| B F-8 | Some revision-15 behaviour had neither a test nor a live-check step | Low | PLAN-GAP | The live check above lists them |
| B N1 | The plan named a "forget to persist" mutation the runner didn't have | Nit | PLAN-GAP | Added to round 16's mutation run |
| B N2 | Stale "switcher" wording in `MainViewModel.Catalog` and App | Nit | HOUSE-RULE | Reworded |
| B N3 | Showing a folder again matched it in SQL, where NOCASE folds only ASCII; hiding matched it in .NET | Nit | HOUSE-RULE | Showing finds the folder as hiding does, then deletes it by id. `HiddenFoldersTests.ShowingAFolderAgain_IgnoresCaseBeyondAscii` |
| B N4 | README left out the `<name> (2)` fallback | Nit | PLAN-GAP | Added |
| B N5 | 921ae4c's message gives harness timings without saying so | Nit | — | Carried (commit messages aren't rewritten); this plan says where the numbers come from |

Refuted or confirmed safe by the reviews: the tile menu's `Closed` handler clears its tile only after the clicked
command has run; Shift+F10 opens the tile menu on a focused tile; empty canvas and section headers get none; no
per-view-model handler is left on the session-long library list; the hand-over reaches the new library's view
model; every add path asks once.

Pre-existing, not introduced here (flagged, not fixed in this branch): a second index of a folder can still start
while one runs if the user changes "Include subfolders" on it, or a file-watcher event fires, mid-index — the same
pause/cancel bookkeeping problem, in 0.8.9 as well. Revision 16 removes the trigger this branch added (New
library's hand-over), not the general race.

### Revision 17 — what changed after review round 17

One fresh non-author review of the round-16 fixes (091f0b6..f78c716): the Medium fix holds (New library's folder
is indexed once; the test is deterministic); NOT CLEAN on two Lows.

| # | Finding | Severity | Blame | Fix, and the test that now fails without it |
|---|---|---|---|---|
| F-1 | Round 16's B F-3 only partly fixed: the binding walk still missed a template's own triggers, values set by any trigger, and the style a style is based on — a per-tile trigger bound through the window, the shape that made switching slow, would have passed | Low | TEST-GAP | The walk follows all of them. `MainWindowTemplateTests.TheBindingWalk_FindsABindingWhereverATemplateCanHoldOne` plants a window binding in each place a template can keep one (eleven places) and requires every one found — so the pin no longer depends on today's tile using each place |
| F-2 | The filter the round-16 fix needed — skip a folder removed during the reconcile's delay — had no test; without it the removed folder is scanned against a root that's gone, and a watcher stays armed on it | Low | TEST-GAP | `BackgroundRefreshTests.AFolderRemovedBeforeTheStartupReconcile_IsNotIndexedOrWatched` |
| Observation | An add whose dialog (folder picker, subfolder question) was still open when a library switch closed the library reached the closed database; the exception would end the app. It came with this branch's library switching (0.8.9 couldn't switch): Add folder's picker from the start, drops once they asked about subfolders (revision 18 corrects "Add folder had it before this branch") | Low (outside the layer) | HOUSE-RULE (work started before a teardown checks it's still wanted) | Every add checks the view model isn't closing — before asking, and again after. `BackgroundRefreshTests.AFolderAddedAfterTheLibraryHasClosed_IsIgnored` (revision 18 tests both checks on Add folder's own path) |
| N-1 | f78c716's message says "every binding in a tile" | Nit | — | Carried (true from this revision) |
| N-2 | §2 still said "refresh the switcher" | Nit | HOUSE-RULE | Reworded |

### Revision 18 — what changed after review round 18

One fresh non-author review of the round-17 fixes (f78c716..06018d2): the code was correct; NOT CLEAN on three
Lows, all test gaps. The binding walk had now been found narrower than its claim three rounds running (B F-3 in
round 16, F-1 in 17, R18-1 in 18), so — as the house rule says after two in a row — the test was redesigned, not
widened again: what is written in the tile's XAML is checked as text, wherever it sits (in the usual spellings —
revision 19), and the walk's claim is limited to what it walks, each place with a planted binding.

| # | Finding | Severity | Blame | Fix, and the test that now fails without it |
|---|---|---|---|---|
| R18-1 | The walk never looked in resource dictionaries (an element's, a template's, a style's) or at the items of a Freezable collection (gradient stops, transforms) — e.g. the tag badge's gradient stop bound through the window passed | Low | TEST-GAP | `MainWindowTemplateTests.TheTileTemplatesXaml_WritesNoBindingThroughTheWindowOrTheProxy` reads the tile template's own text from `MainWindow.xaml` and rejects any window, proxy or `DataContext.` binding in it; the walk also follows resources and Freezable collections |
| R18-2 | Some walk branches had no planted case (MultiTrigger setters, PriorityBinding, setter values that are styles, templates or objects) | Low | TEST-GAP | `…TheBindingWalk_FindsABindingInEveryPlaceItWalks` plants one binding in each of 21 places the walk claims, and requires exactly those 21 found |
| R18-3 | Of "every add checks it isn't closing — before asking and again after", neither check was tested on Add folder's (or a drop's) own path | Low | TEST-GAP | The question is a test seam (`MainViewModel.AskIncludeSubfolders`, the dialog by default). `BackgroundRefreshTests.AddingFoldersToAClosedLibrary_AsksNothingAndAddsNothing`; `…ALibrarySwitchWhileTheSubfolderQuestionIsOpen_AddsNothing` (the switch closes the library inside the question) |
| N-a | Revision 17 said Add folder had the closed-library crash before this branch; 0.8.9 had no library switch | Nit | PLAN-GAP | Corrected above. The first-run add now goes through the same guarded path as every other add |
| N-b | The removed-folder test waited a fixed 800 ms | Nit | TEST-GAP | It now waits for a second, kept folder's reconcile (a positive control), then checks the removed one never started |
| N-c | Test runs leave temp folders | Nit | — | Pre-existing (`TempDir` cleanup is best effort); not this layer |
| N-d | R17's kill is the exception itself | Nit | — | That is the defect's symptom; kept |

### Revision 19 — what changed after review round 19

One fresh non-author review of the round-18 fixes (06018d2..a33b66c): the production code was correct; NOT CLEAN on
one Low — the fourth round on the binding walk's claim. Every place the walk looks now has a planted binding, and
the prose says what the two checks cover and what they don't (revision 20 corrects "every branch": the walk's
repeat guards aren't proved by it, and two limits were unstated).

| # | Finding | Severity | Blame | Fix, and the test that now fails without it |
|---|---|---|---|---|
| R19-1 | Seven walk branches had no planted binding (MultiDataTrigger setters, a template held as a resource, merged dictionaries, a content element's resources, a non-Freezable object a property holds, a set template's DataTemplate triggers and resources); the doc comment also claimed an effect | Low | TEST-GAP | `…TheBindingWalk_FindsABindingInEveryPlaceItWalks` plants 29 bindings — one in each branch, an effect included — and requires exactly those 29 |
| N-1 | The text check can be fooled by unusual spellings (`AncestorType="{x:Type Window}"` as a property element; `{StaticResource ResourceKey=Vm}`; a `</DataTemplate>` in a comment), so "anything written … wherever it sits" overclaimed | Nit | PLAN-GAP | F3 and the test now say "in the usual spellings"; the walk catches the first two in the objects themselves |
| N-2 | Implicit styles from the grid's resources and DynamicResource references reach a tile but aren't checked | Nit | PLAN-GAP | Stated as a limit in F3 and the walk's doc comment; the tile uses neither |
| N-3 | The shared add now marked first run done before adding the folders (a failed add would have lost the offer for the next launch) | Nit | HOUSE-RULE | First run is marked done after the folders are added — for the first-run panel, as before this branch (Add folder, a drop and New library marking it at all is new in revision 15) |
| N-4 | F3's mutation column didn't list the text check, resources or collections | Nit | PLAN-GAP | Listed |
| N-5 | a33b66c's message says "each of the 21 places it claims" | Nit | — | Carried |

### Revision 20 — what changed after review round 20

One fresh non-author review of the round-19 fixes (a33b66c..b0b704f): production code correct (the first-run
order checked through every failure point); NOT CLEAN on one Low, about the plan's words, not the code.

| # | Finding | Severity | Blame | Fix |
|---|---|---|---|---|
| F-1 | "Deleting any branch fails the completeness test" was false: the walk's `Freezable` case duplicated the general object case (removing it changed nothing), and its four repeat guards (`seen`) aren't exercised by any plant | Low | PLAN-GAP | The duplicate case is gone. The claim is now what's proved: every place the walk looks has a planted binding, so removing any part of the walk that finds bindings fails the test; the repeat guards only stop repeats and loops |
| F-2 | Two limits were unstated: the tiles' container style (`TileContainer`) wasn't checked at all, and InputBindings inside a template aren't walked | Nit | PLAN-GAP | The real-tile test now walks the grid's item-container style too (it has no bindings), with a planted case (30 places); InputBindings are stated as a limit — the tile has none |
| F-3 | The first-run order isn't pinned by a test, and "as before this branch" holds for the first-run panel only | Nit | — | Worded precisely in revision 19's N-3 row; the order stays in the exempt view model, checked by reading |
| — | b0b704f's message repeats "one in each branch of the walk" | Nit | — | Carried |

## Revision 21 — Paul's second trial (2026-10-27)

Round 21 was CLEAN at 7f17409 and Paul tried that build (`publish\trial-7f17409`). He reported four things, and
his screenshot showed a fifth:

1. The "indexing" pill is barely noticeable: an informative spinner in the main pane should make indexing obvious.
2. Only the open library shows a count.
3. A top-level folder's count changes only when indexing finishes — confusing, since indexing can take long.
4. A sparse library wastes the screen: sections grow down but not across. Group by longer spans (by year, here) or
   whatever balances the tiles across and down.
5. (From the screenshot.) His Funcular library has one folder, `D:\func`, holding 2,445 items in subfolders; its
   Everything showed 12 — the photos directly in `D:\func` — and the status bar said "12 items". Since July (b0ad1bd,
   when the top level showed folder tiles, for exactly one ticked folder; f5efeb9 the next day, for exactly one
   folder) "Everything" in a library with one folder has meant that folder's
   own top level; 0.8.9 does it too. Every new library now starts with one folder, so it shows at once — and it breaks
   §3's "clicking it shows everything in it". (It also made item 4 look worse: its twelve photos fell on eleven days,
   nearly one photo a section.)

Design:

- **G1** — Everything (the top of a library) shows every item in the library's ticked folders, however many folders
  it has; the folder in the tree still shows only its own photos, as every folder does. Search from the top searches
  the whole library.
- **G2** — while anything is being indexed, the grid shows a card at its bottom: a spinner, "Indexing", what is being
  indexed (the folder and how far, as the pill says), and a progress bar (filling while files are indexed, sweeping
  while it scans). It doesn't block the grid (only the card itself takes clicks). It is the pill's own information,
  bigger, where the user is looking; the pill stays.
- **G3** — every library in the list shows a count. The open one shows its live count, as now. The others show the
  count they had when last open — Aperture records it in the library list when you switch away from a library and when
  you close Aperture. A library not opened since this version shows no count until it has been.
- **G4** — while a folder is indexed, its count in the tree rises as items are added (when the indexer saves a
  batch, every 200 files; read at most once a second per folder since revision 22), not only at the end.
- **G5** — the date grouping (day, week, month or year) takes the grid's width into account. A grouping qualifies if
  its sections, on average, fill at least half of the rows they take (a section of 1 photo in a 12-across grid fills
  1/12 of its row). Among the groupings that qualify, the one nearest twelve sections wins, as before; if none
  qualifies, the one that fills its rows best. How many tiles fit across comes from the grid's width and the tile
  size, so resizing the window or zooming can change the grouping. Paul's Funcular, 12 across: years.

| AC | Behaviour | Tests | Mutation each must kill |
|---|---|---|---|
| G1 | Everything shows every item of a one-folder library, its subfolders' included; the folder itself shows only its own | `LibraryTopTests.EverythingInAOneFolderLibrary_ShowsAllOfIt` (a real view model, STA) | resolve the top of a one-folder library to that folder again |
| G2 | The grid has an indexing card, shown while indexing, with the indexing text and a progress bar bound to the indexing progress | `MainWindowTemplateTests.TheGrid_ShowsAnIndexingCardWhileIndexing` (real `MainWindow`) | unbind the card's visibility; drop its text or progress bar |
| G3 | A library's last count is recorded, kept across another window's changes, read back, ignored if negative; an old list reads as no count; recording for an unknown library changes nothing; the open library's count is recorded through the service; the list shows each library's count | `CatalogRegistryTests` (5 new), `CatalogServiceTests.RecordItemCount_KeepsTheOpenLibrarysCount`, `CatalogBarViewModelTests.Libraries_CarryEachLibrarysLastCount` | don't save the count; drop it on a re-read; don't sanitize it; record it for the wrong library; leave it off the list |
| G4 | A folder's tree count rises while it's being indexed | `LibraryTopTests.AFoldersCount_RisesWhileItIsIndexed` (250 files: 200 seen before the end); `FolderNodeVmTests.Count_ReportsAChange` | update the count only at the end; don't raise the change |
| G5 | Sparse sections in a wide grid group coarser; sections that fill their rows keep the nearest-twelve choice; a narrow grid keeps the old choice; no dates gives years | `FormattingTests` (5 new, 2 updated) | ignore tiles per row; weight the fill by section size; let the finer grouping win when none qualifies and two fill their rows equally; drop the row-fill rule |

`MainViewModel.cs`, `App.xaml.cs` and `MainWindow.xaml.cs` stay exempt from the coverage floor: App recording the
open library's count (on switching away and on exit) and the window telling the view model its width are checked by
reading and by the live check.

Live check (Paul): open Funcular — Everything shows all 2,445, grouped by year or month, not one photo a row; switch to
Main and back — both libraries show a count; add a large folder (or New library) — a card at the bottom of the grid
shows the indexing, and the folder's count in the tree climbs; narrow the window or zoom in — the grouping may get finer.

### Revision 22 — what changed after review round 22

Two non-author lenses over `7f17409..0e1f2be` found one High, three Medium and seven Low findings (`[×2]`: both found it).

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R22-1 High | The indexing card's progress bar bound `Value` and `Maximum` two-way (a `RangeBase` default) to read-only properties, so the window failed as it was shown: Aperture couldn't start. No test showed the window bound to a view model. | TEST-GAP | `Mode=OneWay`, as the status pill already has | `MainWindowShownTests.TheWindow_OpensOnALibrary_ShowingEachLibrarysCount` (the real window, shown off screen, over a real library and library list) | bind them two-way again |
| R22-2 Medium [×2] | The card's spinner started on `Loaded` and turned for good, hidden or not: a constant CPU cost in an idle Aperture. | HOUSE-RULE | The `IndexingSpinner` style turns it while it's visible and removes the animation when it's hidden | `MainWindowShownTests.TheIndexingSpinner_TurnsOnlyWhileItIsShown`; the shown-window test (the card uses the style; idle, it doesn't turn) | keep the animation when hidden; start it on `Loaded` again |
| R22-3 Medium [×2] | A library left (or Aperture closed) before its items were read was recorded as 0 items. | AC-GAP | The view model's `LoadedItemCount` is null until the items are read, and `RecordItemCount(null)` keeps the last count | `LibraryTopTests.ALibrarysCount_IsUnknownUntilItsItemsAreRead`, `CatalogServiceTests.RecordItemCount_OfALibraryNotYetRead_KeepsItsLastCount` | known before the read; never marked read; record null as 0 |
| R22-4 Medium | Every zoom step, and every change in how many tiles fit across, rebuilt the whole view (the spinner shown, the keyboard cursor and the view model's selection reset) even when the grouping stayed. | PLAN-GAP | A build keeps the section sizes it chose from (`DateGroupings`); a new width or zoom rebuilds only if it chooses another grouping | `GridGroupingTests` (a real view model): `TheGridsWidth_ChoosesTheGrouping`, `AWidthThatKeepsTheGrouping_LeavesTheViewAlone`, `ZoomingIntoAnotherGrouping_Regroups`, `AViewBuiltForAnOlderWidth_RegroupsAsItIsApplied`; `FormattingTests.Groupings_*` (2) | regroup on any change in tiles across; one tile across always; no regroup on a resize, on a zoom, or as an older build is applied |
| R22-5 Low | A regroup lost the user's section toggles (collapse 2023, narrow the window to months, widen it to years: 2023 is expanded again), and a day and the week starting on it share a key. | PLAN-GAP | Toggles are kept per grouping (`SectionExpansion`) | `SectionExpansionTests` (3), `GridGroupingTests.ASectionsToggle_OutlastsARegroupAndBack` | one memory for every grouping; only the view's own toggles |
| R22-6 Low | Each saved batch's folder count counts the whole folder on the UI thread, and a run that changes nothing reports its batches together: a 100,000-item folder would freeze the window for about a second at each launch. | OTHER (performance) | Read at most once a second per folder while it's indexed; the count at the end is always read | `LibraryTopTests.AFoldersCount_IsReadAtMostOnceAnInterval` (600 files, an hour's interval: 200 seen, 400 not) | read on every report |
| R22-7 Low | G5's AC proved only `ChooseMode`; the width and zoom that feed it were untested. | AC-GAP | G5 amended (below) | as R22-4 | as R22-4 |
| R22-8 Low | The other libraries' count text in the list was untested. | TEST-GAP | — | the shown-window test reads both rows: "Default (3)" (live, not its recorded 7) and "Work (42)" | show the recorded count on the open library too; bind it to another property |
| R22-9 Low | `SetItemCount`'s revert after a failed save was untested. | TEST-GAP | — | `CatalogRegistryTests.SetItemCount_WhenTheListCannotBeSaved_ReturnsFalse_AndKeepsTheLastCount` | drop the revert |
| R22-10 Low | The AC table gave `FormattingTests` 4 new tests; there were 5. | PLAN-GAP | Corrected | — | — |
| R22-11 Low [×2] | `EffectiveLocation`'s summary was left on `ScopeRecursive`, which had no callers. | HOUSE-RULE | `ScopeRecursive` and its helper `IsUnder` deleted | — (dead code) | — |

G5, amended: … no dates gives years; the grid's width and zoom choose the grouping; a width or zoom that keeps the grouping
leaves the view alone; a view built for an older width regroups as it's applied if the new width groups differently; a
section's toggle survives a regroup and back.

Nits taken: the "at least half" boundary is pinned (`FormattingTests.Buckets_HalfFullRows_Qualify`, kills `>=` → `>`);
the card's visibility converter is asserted; the progress fraction's `else` is back on the fraction; the window also
reports its width when the grid's scrollbar comes or goes; a count of 0 reads back as 0
(`CatalogRegistryTests.ItemCount_OfNone_IsReadBackAsNone_NotAsUnknown`); a test comment's "one a fortnight";
`FolderNodeVm.Count` and the CHANGELOG say only top-level folders climb; §3, revision 21's record and G3's "invalid"
corrected. Carried: during an index without streaming, a folder's tree count includes items in its hidden subfolders until
the end; a hand-edited count that isn't a whole number makes the list unreadable, as `lastOpenedTicks` already does; the
open library's own count in the list stays put during an index without streaming. For Paul (design, unchanged): the card
also shows for the launch reconcile and for re-indexes the watcher starts, and when the grid is scrolled to the end it
covers the middle of the last row.

`MainViewModel.cs`, `App.xaml.cs` and `MainWindow.xaml.cs` stay exempt: App recording `LoadedItemCount`, and the window
reporting its width on a resize and when the scrollbar comes or goes, are checked by reading and by the live check.

Live check (Paul), in addition to revision 21's: Aperture starts and switches libraries; with nothing indexing, the card
is hidden and Aperture idles; zooming that keeps the grouping doesn't flash "Loading…"; collapse a year, narrow the
window until it groups by month, widen it again — the year is still collapsed.

### Revision 23 — what changed after review round 23

Two non-author lenses over `0e1f2be..fc181dd` found one Medium — introduced by revision 22 — and nine Lows.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R23-1 Medium | Revision 22's report of the width when the grid's scrollbar came or went fed the regroup it caused: with the months collapsed (no scrollbar) and the window just past a boundary, the view went to years (a scrollbar: one tile fewer across), back to the collapsed months (none), and so on — about six rebuilds a second at a resting width. Lens B flagged the risk; lens A reproduced it in the real window. | AC-GAP | The grouping's width is the grid's viewport with a scrollbar allowed for, shown or not (`MainWindow.GroupingWidth`), so a scrollbar coming or going can't change it | `MainWindowShownTests.ARegroupThatBringsInTheScrollbar_Settles` (lens A's case, the viewport sized between the two heights), `TheGroupingWidth_AllowsForTheScrollbar_ShownOrNot`, `TheGroupingWidth_IsTheViewport_WhileTheScrollbarShows` | report the viewport as it is; allow for the scrollbar even while it shows |
| R23-2 Low | Every section's state was remembered per grouping, so a section collapsed only by default (an older year) was carried to the next folder grouped the same way, even after other groupings in between. | AC-GAP | Only the user's toggles are remembered across groupings: a section whose state differs from how its build left it (`SectionVm.ExpandedByDefault`); a rebuild in the view's own grouping still keeps every section as it is | `GridGroupingTests.ASectionCollapsedOnlyByDefault_IsntCarriedToAnotherFolder`, `ASectionLeftOpen_StaysOpenAsPhotosArriveAboveIt` (20 photos arrive in 2020: 2019, open by default before, stays open) | remember every section's state; only toggles in the view's own grouping too |
| R23-3 Low | The width came from the first section's wrap panel, which keeps its width once collapsed: the grouping stopped following the window (revision 21). | TEST-GAP | `GroupingWidth` reads the grid's scroll viewport | `MainWindowShownTests.TheGrouping_FollowsTheWindow_WithItsFirstSectionCollapsed` | take the width from the first wrap panel |
| R23-4 Low | The open library's count in the list read "(0)" until its items were read (revision 21). | AC-GAP | It binds `LoadedItemCount` (none until known) | the shown-window test, before and after the read | bind `TotalItemCount` again |
| R23-5 Low | R22-1 said `Value` and `Maximum` bind two-way by default; only `Value` does (`Maximum`'s `Mode=OneWay` is harmless). The commit message says the same. | PLAN-GAP | Corrected here | — | — |
| R23-6 Low | "At most once a second per folder" had no test; one interval for the whole library passed. | TEST-GAP | — | `LibraryTopTests.EachFoldersCount_HasAnIntervalOfItsOwn` | one interval for the library |
| R23-7 Low | An ungrouped build's clearing of the view's grouping was untested: kept, a resize rebuilt an ungrouped view, and turning sections back on lost the toggles. | TEST-GAP | — | `GridGroupingTests.AViewWithoutDateSections_IgnoresTheWidth`, `ASectionsToggle_OutlastsTurningSectionsOffAndOn` | keep the grouping across an ungrouped build |
| R23-8 Low | The progress fraction's `else` (a revision-22 nit) was untested: on the count's `if`, every report between count reads would flip the bars to the sweep. | TEST-GAP | — | `LibraryTopTests.ReportsBetweenCountReads_KeepTheProgressBarFilling` | the `else` on the count |
| R23-9 Low | "A zoom that keeps the grouping leaves the view alone" was untested. | TEST-GAP | — | `GridGroupingTests.AZoomThatKeepsTheGrouping_LeavesTheViewAlone` | a zoom rebuilds on any change in tiles across |
| R23-10 Low | The shown-window test's summary claimed it catches any two-way binding onto a read-only property; it catches those made in its scenario. | HOUSE-RULE | Summary narrowed (no such binding exists today; lens B checked every target) | — | — |

G3, amended: … a library left before its items are read keeps its last count; the open library shows no count until its
items are read. G5, amended: … a view without date sections ignores the width; the width is the grid's viewport with a
scrollbar allowed for, so a regroup settles; across groupings only the user's toggles are remembered.

Also: "a width that keeps the grouping" is now checked as no build being requested (`MainViewModel.BuildGeneration`,
counted at the request) instead of none applied within a second; R22-4 and R22-5 are AC-GAP by the first-match rule (G5
permitted both); the G4 bullet gives the interval; `IndexCountInterval`'s summary says what is read at the end; the
mutation runner keeps each row's log and `why13.py` shows each failure's message. The shown-window tests size their
windows for a 1024 × 768 desktop (the folder pane at its narrowest), as on the CI runner — UNVERIFIED there until CI runs.
Carried: `_countReadAt` is never cleared (a run starting within a second of the last read skips its first report);
after a failed run without streaming, the tree node can stay up to one interval behind until the tree is next built.

Live check (Paul), in addition: collapse all but the newest month, then widen the window slowly — the grouping changes
once at a boundary and stays; switch libraries — the open one's count appears once its items are read, never "(0)".

### Revision 24 — what changed after review round 24

Two non-author lenses over `fc181dd..3cdb56c`: no High or Medium; R23-1's loop verified gone (lens A swept 51 widths a
pixel apart across the boundary: no rebuild at rest). Seven Lows, two of them found by both.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R24-1 Low [×2] | R23-2 was partial: a toggle was inferred from a state differing from its build's default, so a default carried in from another folder in the same grouping, one kept by a rebuild, or one a sort change flipped read as the user's toggle and was remembered across groupings; and a default-collapsed section still went straight to the next folder grouped the same way (as in 0.8.9). | TEST-GAP | A toggle is recorded as it's made: `SectionVm.Toggled`, set by `IsExpanded`'s setter (the header's toggle, the keyboard); a build sets a section with `SectionVm.Start`. Only the view as it is — the same place (`LocationKey`) in the same grouping — carries every section's state on; another folder, a search or another grouping gets only the user's toggles | `SectionVmTests` (4); `GridGroupingTests.ASectionCollapsedOnlyByDefault_IsntCarriedStraightToAnotherFolder`, `AStateCarriedToAnotherFolder_DoesntBecomeAToggle`, `ASectionKeptOpenByARebuild_IsntRememberedAsAToggle` (lens B's probes P1–P3) | remember every section; a section carried on starts as a toggle; the user's change isn't a toggle; a build's state is; the view's states go to another folder |
| R24-2 Low | The grid's `SizeChanged` came before layout, with the old viewport beside the new scrollbar: a height change that brought the scrollbar in or took it away asked for a rebuild in an unchanged grouping (revision 23). | TEST-GAP | The width is reported on `ScrollChanged` only (after layout), and `GroupingWidth` is the scroll viewer's width less its padding and a scrollbar — it never reads the viewport, so the scrollbar's real width at other display scales can't matter either (lens A's hardening) | `MainWindowShownTests.AHeightChangeThatBringsInTheScrollbar_AsksForNoBuild`, `AHeightChangeThatTakesTheScrollbarAway_AsksForNoBuild` (lens B's P4–P5) | round 23's width (the viewport, less a scrollbar when none shows) reported on `SizeChanged` again — a `SizeChanged` report alone no longer matters (an equivalent mutant: the width never reads the viewport) |
| R24-3 Low | Keyboard navigation's column count also came from the first section's wrap panel, which keeps its width once collapsed: Down went two rows (from 0.8.4; R23-3's sibling). | HOUSE-RULE | `WrapViewportWidth` reads the first shown section's panel | `MainWindowShownTests.TheKeyboardsColumns_FollowTheWindow_WithItsFirstSectionCollapsed` (against the columns laid out) | take the first panel, shown or not |
| R24-4 Low | Everything's count read "(0)" until the library's items were read (from before this branch; R23-4's sibling). | HOUSE-RULE | It binds `LoadedItemCount`: blank until known | the shown-window test, before and after the read | bind `TotalItemCount` again |
| R24-5 Low | The open library's count reported again as its items change (`LoadRows`) had no test. | TEST-GAP | — | `LibraryTopTests.ALibrarysCount_IsReportedAgainAsItsItemsChange` | don't report it after a re-read |
| R24-6 Low | Nothing showed `BuildGeneration` counts, though three "no rebuild" tests rest on it; its summary claimed every build. | TEST-GAP | Summary says what it counts (the startup build and each debounced rebuild, as asked for) | `GridGroupingTests.TheGridsWidth_ChoosesTheGrouping` asserts the request is counted | count nothing |
| R24-7 Low [×2] | `GroupingWidth` had been put between `WrapViewportWidth`'s summary and its method: two summaries on one, none on the other. | HOUSE-RULE | Each has its own | — | — |

G5, amended again: … a section's toggle — the user's own, recorded as it's made — survives a regroup and back; another
folder, a search or another grouping gets only the user's toggles; the width is the scroll viewer's less its padding and
a scrollbar, read after layout.

Also: `SetGridWidth`'s summary; the window tests made by `WithWindow` fit a 1024 × 768 desktop (the round-22
`TheWindow_Opens…` asks for 1200 × 800 and would be shrunk there, which nothing it checks depends on); the mutation
runners count a row KILLED when a named test fails (round 23's logger change had every row reading NO RESULT while the
"failed" column showed each kill); a test comment on when a folder's end count is read. Record: rounds 22 and 23's commit
messages say `Value` and `Maximum` both bind two-way by default (only `Value` does; R23-5). Carried: before the grid's
first layout `GroupingWidth` falls back to the list's width, 2 px wider than the viewer's (replaced on the first
`ScrollChanged`). Observed once: `BackgroundRefreshTests.AFolderRemovedBeforeTheStartupReconcile_IsNotIndexedOrWatched`
failed in one full run under heavy load (two reviewers and the mutation runner at once), message not kept; lens A ran its
class 45 times under load with no failure, and nothing in this range touches what it tests — UNVERIFIED cause.

Live check (Paul), in addition: go from a folder whose older year is collapsed by default to one with only that year —
it opens; collapse the newest year, narrow the window, press Down from the next year's first photo — one row down;
Everything's count appears once the library is read.

### Revision 25 — what changed after review round 25

Two non-author lenses over `3cdb56c..7c64c90`: no High or Medium; three Lows, all found by both lenses, and nits. Every
round-24 finding verified dispositioned; R24-2 to R24-7 resolved; R24-1 partial (below).

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R25-1 Low [×2] | R24-1 was partial: "the view as it is" was keyed by `LocationKey`, which is `search` for every search, so a search refined (or replaced) by another carried every section's state on — a section collapsed only by default came up collapsed as the only section of the new results. The Pictures/Videos filter did the same. | TEST-GAP | The carry is keyed by what the view shows (`ViewKey`: the place, the search text and the Pictures/Videos filter); `LocationKey` still keys scroll memory by place. A filter change counts as another view (the owner may rule otherwise) | `GridGroupingTests.ASearchRefinedToAnother_GetsOnlyTheUsersToggles`, `ThePicturesAndVideosFilter_GetsOnlyTheUsersToggles` | any search the same view; the filter the same view |
| R25-2 Low [×2] | Nothing tested that a user's toggle keeps travelling: that one carried to another place stays a toggle, and that a rebuild in its own place keeps it one. | TEST-GAP | — | `GridGroupingTests.AToggle_GoesOnThroughAFolderItWasCarriedTo` (A → C → D), `AToggle_OutlastsARebuildInItsPlace_AndGoesToAnotherFolder` | a toggle carried on isn't one; a rebuild in place drops it |
| R25-3 Low [×2] | R24-2's named mutation (report on `SizeChanged` again) was an equivalent mutant once the width stopped reading the viewport (the author found it from the runner, as did both lenses). | PLAN-GAP | R24-2's row names what the height tests kill: round 23's width reported on `SizeChanged`. And since a resize can leave the viewport's width unchanged (the scrollbar going as the grid narrows by its width — no `ScrollChanged`), the width is reported on the grid's `SizeChanged` again as well (harmless now, closing that edge) | — | — |

Found while checking the observed-once failure (lens B, with its message): `BackgroundRefreshTests.AFolderRemovedBeforeTheStartupReconcile_IsNotIndexedOrWatched`
failed with "The transaction object is not associated with the same connection object as this command" in
`HiddenFolderStore.GetAll` ← `LibraryService.GetContents` ← `InitializeViewAsync`. The library's one FunkyORM provider is
shared between threads: the startup read runs on the thread pool, and a transaction opened on the provider meanwhile on
the UI thread (removing a folder — `RootStore.Remove` — or saving tags) is seen by the read. It ends Aperture (the
startup read is `async void`). 0.8.9 has it too (its startup read went through the same provider), so it is not this
branch's; for Paul to schedule — a reader's own provider for the startup read, or one lock around the provider. The test
now waits for the startup read before it removes the folder, so it tests the reconcile delay it's about.

Nits taken: comments on what reports the width (`SizeChanged` and `ScrollChanged`; "no ScrollChanged to tell it" on a
switch) and on what counts as the view as it is (not "a resize": one that keeps the grouping doesn't rebuild); the
scrollbar-away test's window stays within a 768-high desktop (it reached 811 px); runner labels match this plan's numbers.
Corrected record: only round 22's fc181dd says `Value` and `Maximum` both bind two-way by default; the "Carried" fallback
of revision 24 can't run (both reports come after layout) — dropped.

Live check (Paul), in addition: search for something, then for something narrower showing only an older year — it
opens; collapse a year, go to two other folders that have that year in turn — it stays collapsed in each.

### Revision 26 — what changed after review round 26

One non-author lens over `7c64c90..391784e` (behaviour and proof): no High or Medium; four Lows, seven nits. R25-2
resolved; R25-1 and R25-3 partial (below); every R25 mutation re-run and killed at its assertion.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R26-1 Low | The restored `SizeChanged` report had no test: in one layout the grid can narrow by a scrollbar's width as the scrollbar goes, leaving the viewport's width as it was (no `ScrollChanged`). | TEST-GAP | The grid's `SizeChanged` is now the only report (the `ScrollChanged` one was an equivalent mutant: the scroll viewer's width changes only with the grid's, and is laid out when `SizeChanged` comes) | `MainWindowShownTests.ANarrowerTallerGrid_InOneLayout_IsReported` (the lens's probe) | no `SizeChanged` report; it reads the wrap panel |
| R26-2 Low | `ViewKey`'s Videos half was untested (only Pictures was). | TEST-GAP | — | `GridGroupingTests.EveryChangeTheUserMakesToWhatsShown_IsAnotherView` | the Videos filter not in the key |
| R26-3 Low | The folders' checkboxes changed what the view shows without changing `ViewKey`: unticking a folder left Everything's only section collapsed by default. Enumerating the siblings: a folder removed, its subfolders taken in or left out, a subfolder hidden, videos indexed or not (adding a folder goes to it, another place already). | AC-GAP | `ViewKey` takes a count each of those user changes adds to (`_shownChanges`): every change the user makes to what's shown is another view; photos arriving and a sort change aren't | `GridGroupingTests.UntickingAFolder_GetsOnlyTheUsersToggles` (the lens's probe), `EveryChangeTheUserMakesToWhatsShown_IsAnotherView` (each change — a folder removed from Everything, where the place stays — and a sort change that isn't one) | each of the five changes not counted |
| R26-4 Low | Revision 25's race write-up understated it: the failing read (`HiddenFolderStore.GetAll`) is this branch's, and it comes about a second after the union read — on every library switch, while the window takes input — so this branch widened the window; and the deferred thumbnail prune (`LibraryService.PruneThumbnailCache` → `ItemStore.TotalCount` on the thread pool, a minute in) is a second off-thread user of the provider (in 0.8.9 too): a tag save under way then can end Aperture as well (read in the code, not run). | PLAN-GAP | Record corrected (below) | — | — |

The provider race, corrected, for Paul: the library's one FunkyORM provider is shared between threads, and a transaction
opened on it on the UI thread (removing a folder, saving tags) is seen by an off-thread read — the startup read (two
reads on this branch, the second about a second after the first, on every switch) or the deferred prune — which then
throws, ending Aperture. The mechanism is in 0.8.9; this branch widened the startup window. One lock around the
provider covers both readers (a reader's own provider for the startup read alone would miss the prune). Also in that
window, from 0.8.9: the startup read assigns its rows before checking it's still current, so a folder removed during it
can come back in the view (and its count) until the next data change. Both for Paul to schedule.

G5, amended again: … another folder, search, Pictures/Videos filter, choice of folders or files, or grouping gets only
the user's toggles. Nits taken: the width-report comments; `SectionVm.Toggled`'s summary ("other views"); the
scrollbar-away test's window within 768 px (it reached 771); revision 25's header (three Lows); the live check names
folders that have the year. Not taken: "Beach" and "beach" are different views (as `CommitSearch` already treats them).

Live check (Paul), in addition: untick one of two folders on Everything — an older year that was collapsed only by
default, now the newest, opens.

### Revision 27 — what changed after review round 27

One non-author lens over `391784e..8f84698`: no High or Medium; two Lows. R26-1 and R26-2 resolved; R26-3 resolved for
the changes made in one step (all five rows killed at 8f84698) but not for those carried out by re-indexing (R27-1);
R26-4's corrected write-up still wrong about one mechanism (R27-2).

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R27-1 Low | The count was taken once, where the change was made; a change carried out by re-indexing every folder (videos indexed or not) lands as one rebuild per folder, and every rebuild after the first was "the view as it is": with two folders whose re-indexes end apart, Everything's only remaining section stayed collapsed by default. Subfolders taken in or left out likewise, if a rebuild came between the change and its re-index's end. | TEST-GAP | A re-index that carries out such a change counts again as it lands, before the rebuild it asks for (revision 28 refines this: counted only where it lands, and only if it changed something) | `GridGroupingTests.AReindexCarryingOutAChangeToWhatsShown_CountsAgainWhereItLands` (the count taken again as each re-index lands, at both call sites). The lens's two-folder probe reproduces the symptom only when the two re-indexes land in separate rebuilds — under load; here they merged inside the debounce, so it can't be a test that fails dependably | not counted where it lands; either call site's run not marked |
| R27-2 Low | The race write-up said the off-thread reader throws, ending Aperture. For the prune that is false — its failures are caught. What can end Aperture is the UI thread's side: a read that opens while the UI's transaction is open joins its connection, and the UI's next call in that transaction throws ("A concurrent operation is already using the transactional connection"; read in FunkyORM 3.10.1, not run). | PLAN-GAP | Record corrected (below) | — | — |

The provider race, corrected again, for Paul: the library's one FunkyORM provider is shared between threads. While the
UI thread has a transaction open on it (removing a folder, saving tags), an off-thread read — the startup read (two reads
on this branch, the second as soon as the union read ends, on every switch) or the deferred prune (a minute in) — either
throws itself (the startup read: `async void`, ending Aperture) or joins the transaction's connection, so the UI's next
call in that transaction throws (ending Aperture; the prune's own errors are caught). The mechanism is in 0.8.9; this
branch widened the startup window. A fix must hold one lock across the UI's whole transaction (begin to commit or
rollback) and around each off-thread read — a per-call lock still lets a read join an open transaction, and catching in
the readers fixes nothing. The stale-rows overwrite in the same window (revision 26) stands.

Also: the mutation runner gains a row for the width reported on `ScrollChanged` only, which
`ANarrowerTallerGrid_InOneLayout_IsReported` kills at the assertion R26-1 names (the "no `SizeChanged` report" row
fails earlier, at its set-up). Not a finding: a library switch's own `SetGridWidth` is checked by reading and the live
check (an exempt shell), as before.

### Revision 28 — what changed after review round 28

One non-author lens over `8f84698..505aeef`: no High or Medium; three Lows (one introduced by revision 27), six nits.
R27-2 resolved (verified against FunkyORM 3.10.1's source); R27-1 partial; dropping the two-folder test verified right.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R28-1 Low (fix-introduced) | A marked re-index that changed nothing (videos off in a folder with none; a run that throws) took the count but rebuilt nothing; the count was left for the next rebuild of the same view — photos arriving, a sort — which then collapsed every section nobody toggled. | TEST-GAP | A change carried out by re-indexing counts only as a run lands it and changed something (`dataChanged`) | `GridGroupingTests.AReindexThatChangesNothing_LeavesNoCountBehind` | count a run that changes nothing |
| R28-2 Low | The count taken where such a change was made (the `IncludeVideos` setter, `OnRootRecursiveChanged`) had no rebuild of its own either: subfolders left out of a folder with none left a count over the same way. | TEST-GAP | No count where it's made: the folder is marked as having the change outstanding (`_shownChangeOutstanding`), and the run that lands it counts | the same test | count where the change is made too |
| R28-3 Low | A run paused (or stopped) before it carried the change out skips its clean-up, and the run that resumes it carried the change out unmarked — "the view as it is": Everything's only section stayed collapsed by default. | TEST-GAP | The outstanding mark stays with the folder until a run of it ends uncancelled, so a resumed run lands it | `GridGroupingTests.AChangePausedAndResumed_CountsWhereTheResumedRunLands` (the pause lands before the run, given 400 new photos to index) | a paused run counts; a paused run drops the mark |

Comments and the plan no longer say a marked run "ends with a rebuild" (R28 nit 3). Record: revision 27's header —
two of R26-3's five changes are carried out by re-indexing and were partial (R28-2); R27-2's "the UI's next call in that
transaction throws" is "can throw" (only if the read is still running), and a commit can close the shared connection
under a running read, so the read throws instead — the fix sentence (one lock across the whole transaction) covers both;
dc0b65a's commit message names the two-folder test 505aeef removed. Carried: the outstanding mark is in memory, so a run
stopped (not paused) whose change finishes only at the next launch's reconcile shows it as the same view; "photos
arriving isn't another view" is pinned through the startup reconcile and a refresh, not the folder watcher (the same
code path).

Live check (Paul), in addition: turn "include videos" off and pause indexing straight away, then resume — when it ends,
an older year left alone opens as the newest.

### Revision 29 — Paul's ruling on date sections, after review round 29

Round 29 (one non-author lens over `505aeef..2f48afe`) found three more Lows in the rule revisions 23–28 had built — that a
rebuild of "the view as it is" (the same place, search, filter and choice of folders, in the same grouping) keeps every
section's state, while any other build keeps only the user's toggles: R29-1 (introduced by revision 28) a folder's
re-index mark taken by whichever run of the folder landed first; R29-2 a run that throws clearing the mark without
carrying the change out; R29-3 the clearing half of R28-1 untested. Six rounds in a row had found the edges of that rule,
most introduced by the round before. Asked to choose, Paul ruled (2026-10-27): **untouched sections follow the default on
every rebuild; only the sections the user expanded or collapsed are remembered.**

So the rule is now one line: every build starts each date section as the user left it, if they did (remembered per
grouping, `SectionExpansion`, recorded as made, `SectionVm.Toggled`), and otherwise by the default — the newest ~2
screens open. The machinery that decided "the view as it is" is gone: `ViewKey`, the what's-shown count and the
per-folder outstanding marks, and the view's states carried into a build. R29-1, R29-2 and R29-3 are moot: the code they
are about no longer exists. The visible difference from revisions 23–28 (and from 0.8.9, which carried every state): as
photos stream in above an older section the user hasn't touched, it opens and closes with the default; expanding it
once keeps it open.

| Test | What it pins now |
|---|---|
| `GridGroupingTests.ASectionNobodyToggled_FollowsTheDefaultAsPhotosArrive` (was `ASectionLeftOpen_StaysOpenAsPhotosArriveAboveIt`, inverted) | an untouched section follows the default as photos arrive |
| `AToggle_OutlastsARebuildInItsPlace_AndGoesToAnotherFolder`, `AToggle_GoesOnThroughAFolderItWasCarriedTo`, `ASectionsToggle_OutlastsARegroupAndBack`, `ASectionsToggle_OutlastsTurningSectionsOffAndOn`, `SectionVmTests` | a toggle is kept through rebuilds, places and groupings |
| `ASearchRefinedToAnother_GetsOnlyTheUsersToggles`, `ThePicturesAndVideosFilter_GetsOnlyTheUsersToggles`, `UntickingAFolder_GetsOnlyTheUsersToggles`, `ASectionCollapsedOnlyByDefault_IsntCarriedStraightToAnotherFolder`, `AStateCarriedToAnotherFolder_DoesntBecomeAToggle` | a default isn't carried anywhere |
| Removed with the machinery | `EveryChangeTheUserMakesToWhatsShown_IsAnotherView`, `AReindexCarryingOutAChangeToWhatsShown_CountsAgainWhereItLands`, `AReindexThatChangesNothing_LeavesNoCountBehind`, `AChangePausedAndResumed_CountsWhereTheResumedRunLands`, `ASectionKeptOpenByARebuild_IsntRememberedAsAToggle` |

Mutation rows (runner `mutate20.py`, 65 rows): the rows for the removed code are gone; new ones — untouched sections
remembered too (killed by the five "defaults aren't carried" tests and the photos-arriving test), a toggle carried into a
build not marked as one, a toggle not carried into the build.

G5, amended again: … the grid's width and zoom choose the grouping (the width the scroll viewer's, less its
padding and a scrollbar, reported on the grid's resize); a width or zoom that keeps the grouping leaves the view alone; a
view built for an older width regroups as it's applied if the new width groups differently; every build opens each date
section as the user left it, if they did, and otherwise by the default.

Live check (Paul), replacing revisions 23–28's section checks: collapse a year, narrow the window until it groups by
month, widen it again — still collapsed; go to another folder with that year — collapsed there too; a section you haven't
touched opens and closes by the default (the newest ~2 screens open) after any change.

### Revision 30 — what changed after review round 30

One non-author lens over `2f48afe..b557fc0`: R29-1..3 verified moot (nothing of the removed machinery left); every
section mutation re-run and killed; four Lows and nits.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R30-1 Low | Only collapsing a section was tested: opening one — the remedy revision 29 offers — wasn't. | TEST-GAP | — | `GridGroupingTests.ASectionTheUserOpened_StaysOpenAsPhotosArrive` (the lens's probe) | only a collapse is a toggle; a remembered open ignored; every toggle remembered as collapsed |
| R30-2 Low | The CHANGELOG ("in the next folder or search that has it"), G5 and a comment said a toggle is kept; a view in the same grouping without that section replaced the grouping's memory and forgot it. | TEST-GAP (revision 31: G5 forbade it) | Toggles are kept for the session: each build adds the view's toggles to those remembered instead of replacing them (`CurrentToggles`), so the claims are true; the CHANGELOG says "other folders and searches that have it" | `GridGroupingTests.AToggle_IsKeptThroughAFolderWithoutItsSection` (the lens's probe) | the view's toggles replace those remembered |
| R30-3 Low | Comments still described the removed rule (`SectionVm`'s summary: "state survives incidental rebuilds"; `Toggled`: "for other views"; a test header: "the view as it is"). | HOUSE-RULE | Reworded | — | — |
| R30-4 Low (for Paul) | A cost of the ruling revision 29 didn't name: if the selected photo is in an untouched section that closes by default during a rebuild (photos arriving above it), the selection is left on a hidden photo and the keyboard's next move starts from the first photo of the grid. | AC-GAP | Recorded here and in the live check; for Paul: accept it, or have the section holding the selection count as touched | — | — |

Carried (from 0.8.9, the same there): a toggle made while a build computes (a click during "Loading…") is lost — the
build replaces the sections the toggle was made on. Revision 28's two carried items are moot with the code they were
about. Nits taken: revision 29 says untouched sections open and close with the default, and G5 "amended again".

Live check (Paul), in addition: open an older section closed by default, add photos above it — it stays open; collapse a
year, go to a folder without it, then to one with it — still collapsed; select a photo in an older untouched section and
let photos arrive above it — the section may close (R30-4).

### Revision 31 — what changed after review round 31

One non-author lens over `b557fc0..7ca09af`: R30-1, R30-3 and R30-4 verified; R30-2's merge correct; two Lows the merge
left untested, and nits.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R31-1 Low | The merge's precedence — a toggle the user makes now beats the one remembered — was untested: remembered-wins survived every test. | TEST-GAP | — | `GridGroupingTests.ARememberedToggle_TheUserChangesInAnotherFolder_IsKeptAsChanged`, `AToggleChangedBackInItsOwnFolder_IsKeptAsChanged` (the lens's probes) | the remembered toggle beats the newer |
| R31-2 Low | With the merge, the memory is the authority: a carried section marked `Toggled` changed nothing but a flag check — except that, if a streaming build merged a newer toggle while an older build was still computing, the older build's carried flag wrote the stale value back for good. Revision 29's row "a toggle carried into a build not marked as one" had become an equivalent mutant (the author found it; the lens agreed on the mechanism). | TEST-GAP | A build starts every section untoggled (`SectionVm.Start(expanded)`): `Toggled` means the user changed it since its build made it, and the view model's memory is what later builds start from. The flag check became a check of the section's state | `SectionVmTests.ABuildStartingASectionToggled_DoesntMakeItAToggle` (replaces `AToggleCarriedOn_StaysAToggle`); at the build, `AToggle_OutlastsARebuildInItsPlace_AndGoesToAnotherFolder`'s flag check (revision 32) | a build's state is a toggle |

The runner drops two rows equivalent since revision 30 — "the grouping kept across an ungrouped build (toggles)" (a
stale grouping adds nothing and keeps the memory; its width half stays) and "a toggle carried into a build isn't a toggle"
(the code is gone) — and gains "a remembered open ignored" (killed by `ASectionTheUserOpened_StaysOpenAsPhotosArrive`;
revision 30 named it without a row) and the R31 rows. Nits taken: R30-2's blame is TEST-GAP (G5 forbade the forgetting);
toggles last until the user changes them, switches libraries or closes Aperture (a new view model starts empty) — the
comment and the CHANGELOG say so; the photos-arriving test cites the opened case too; the status line.

### Revision 32 — what changed after review round 32

One non-author lens over `7ca09af..a9ca792`: the code correct; four Lows, all tests or prose.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R32-1 Low | Both R31-1 tests ended where the expected state was also the destination's default, so a changed toggle forgotten (falling back to the default) passed them. | TEST-GAP | — | The tests end where the default differs: D with 2019 closed by default (opened by the user: open); a search for 2019's photos alone (closed by the user: closed) | a changed remembered toggle forgotten |
| R32-2 Low | R31-2's fix had no test at the build (the unit test calls `Start` on a toggled section, which builds never do), and the row cited the R31-1 tests, which pass with the pre-fix behaviour. | TEST-GAP | Row corrected (revision 31) | `AToggle_OutlastsARebuildInItsPlace_AndGoesToAnotherFolder`: after the rebuild, the section started from the remembered toggle isn't toggled | sections started from memory marked toggled |
| R32-3 Low | "Opened then closed in one view is still a toggle" (what the `Toggled` summary and the CHANGELOG mean) was untested. | TEST-GAP | — | `SectionVmTests.ChangedAndChangedBack_IsStillAToggle` | a second change cancels the toggle |
| R32-4 Low | `Toggled`'s summary said "untoggled sections follow the default" — false since revision 31 for a section started from memory. | HOUSE-RULE | "a section the user hasn't toggled anywhere follows the default" | — | — |

Nits taken: `ABuildStartingASectionToggled_DoesntMakeItAToggle`'s comment; `_expansion`'s ("taken from the view as each
build starts"). Record: with the merge, a toggle made while a streaming build and a debounced one interleave is kept in
memory and returns on the next build (revision 30's carried item covers the loss in the view that's up).

### Revision 33 — what changed after review round 33

One non-author lens over `a9ca792..ea55c68`: the fix layer has no findings of its own (round 32's four verified, with
round 32's mutants and the lens's own); one Low from the cumulative range, and nits.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R33-1 Low | Toggles kept per grouping through later builds were tested only for years: reading the years' memory whatever the grouping survived every test (a month's toggle was then kept in the first folder it reached, forgotten in the next). | TEST-GAP | — | `GridGroupingTests.AMonthsToggle_GoesOnThroughAFolderItWasCarriedTo` (the lens's probe) | the merge reads the years' memory |

Also, from the lens's observation (the default is older than this branch, but since revision 29 applies on every
rebuild): the newest section opens even when it alone holds more than the newest ~2 screens —
`ASectionNobodyToggled_FollowsTheDefaultAsPhotosArrive` now checks 2020 (70 photos) is open; counting a section's own
photos into its default fails it. Nits taken: `_expansion`'s comment reflowed; `SectionVmTests`' summary (toggles are
remembered per grouping, not carried to other groupings). Record: revision 32's runner gained the R31-1 and R32 rows (73
rows); this one adds the R33 rows (75).

### Revision 34 — what changed after review round 34

One non-author lens over `ea55c68..f7097f2`: the fix layer has no findings (R33-1 and the observation verified, the
build-side twin of R33-1's mutation killed too; 33 runner rows re-run, all killed); one Low from the cumulative range,
and nits.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R34-1 Low | Which grouping the view's toggles are filed under was untested at the view model — the case per-grouping memory exists for (a day and the week starting on it share a key; R22-5). Filing them under the next build's grouping survived every test: the regroup tests all went years↔months, whose keys never collide. | TEST-GAP | — | `GridGroupingTests.AMondaysToggle_DoesntCloseItsWeek_AndIsThereBackInDays` (the lens's probe) | toggles filed under the next build's grouping |

Nits taken: `SectionVm.IsExpanded`'s summary says changing it is the user's toggle (setting the state it has isn't);
the runner is `mutate25.py` (76 rows; revision 33's was `mutate24.py`). Observations, not findings: the default's
threshold (~60 items, "~2 screens", approximate by design) and a collapsed section's room in it are as in 0.8.9.

### Revision 35 — what changed after review round 35

One non-author lens over `f7097f2..791ba6e` (and a fresh sweep of the cumulative range): the fix layer has no findings
(R34-1 verified; the runner's 76 rows all killed in the lens's own run); one Low from the cumulative range, and a nit.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R35-1 Low | G4's mid-run tree count leaves hidden subfolders out (F6's rule), but nothing tested it there: counting them in survived every test — after a launch's reconcile of 200 files or more (the indexer reports every 200; revision 36), a folder with a hidden subfolder would show its hidden items in its count until the tree was next built. | TEST-GAP | — | `LibraryTopTests.AFoldersCountWhileItIsIndexed_LeavesItsHiddenSubfoldersOut` (the lens's probe) | the mid-run tree count includes hidden subfolders |

Nit taken: the CHANGELOG's default reads "the newest ~2 screens open" (as the README). Runner `mutate26.py`, 77 rows.

### Revision 36 — what changed after review round 36

One non-author lens over `791ba6e..2b03399`, with a sweep of the cumulative range (each criterion mapped to its tests):
the fix layer changed no code, and R35-1's test killed its mutant but could pass without a read; three more behaviours
untested; nits. Tests only, and one summary.

| # | Finding | Blame | Change | Tests | Mutation each must kill |
|---|---|---|---|---|---|
| R36-1 Low | R35-1's test could pass with no count read mid-run: both its checks hold if no report comes (with batches of 500, the R35-1 defect passed it), and its wait for the run to start never saw it (5 s a run). | TEST-GAP | — | `LibraryTopTests.AFoldersCountWhileItIsIndexed_LeavesItsHiddenSubfoldersOut` waits for the folder's mid-run report ("indexing 200 / 230", its first, so its count is read) and the run's end | no report mid-run (batches of 500) |
| R36-2 Low | A width reported before the first view (the window's first layout, during the startup read) mustn't supersede the startup build and its folder tree; requesting a build then survived every test. | TEST-GAP | — | `GridGroupingTests.AWidthBeforeTheFirstView_LeavesTheStartupBuildAndItsFolderTree` | a width before the read requests a build |
| R36-3 Low | G3's count "recorded when you switch away" lands on the library being left only because the switch activates its target after `PreparingToSwitch`; untested. | TEST-GAP | `PreparingToSwitch`'s summary says the library being left is still the active one | `CatalogServiceTests.RecordItemCount_AsASwitchPrepares_GoesToTheLibraryBeingLeft` (the lens's probe) | the target activated before `PreparingToSwitch` |
| R36-4 Low | G4's "at most once a second" held only for the interval the tests set (an hour); a default of zero survived every test. | TEST-GAP | — | `LibraryTopTests.AFoldersCount_IsReadAtMostOnceASecond_ByDefault` | the interval's default is zero |

Nits taken: a negative count is saved as none (`CatalogRegistryTests.ItemCount_BelowNone_IsSavedAsNone`; mutation: saved
as is); R35-1's row says the mid-run read needs a reconcile of 200 files or more. Record: 2b03399's message says the test
checks the count "during the run", which R36-1 made true. Runner `mutate27.py`, 82 rows.

## Out of scope (future)

- Named saved views **within** a catalog (Option A) — complementary, can layer on later.
- Moving/copying items *between* catalogs.
- Shared window geometry across libraries (see §2).
- A catalog-picker launch screen (vs. always opening the last-active catalog).
