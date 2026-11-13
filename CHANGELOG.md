# Changelog

All notable changes to Aperture Image Browser are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and releases follow [SemVer](https://semver.org/).

## [Unreleased]

### Added
- **Videos play in quick view.** Open quick view on a video (or move to one) and it plays. `Space`
  or a click on the video plays and pauses it. At its end it stops; `Space` or a click plays
  it again from the start. Its controls show play/pause, mute and a volume slider. The very
  first video plays muted; after that, each video starts with the sound you last left (muted
  or not, and the volume), in any library and after a restart. `Enter` or a double-click still
  opens it in its default app, and pauses it in quick view. A video Windows can't play here
  keeps its still frame and says so. On a photo, `Space` still closes quick view.
- **Drag to reorder.** Drag a library in the **LIBRARIES** list, or a top-level folder under
  **FOLDERS**, to put it where you want it. The order is kept (the libraries' with the library
  list, each library's folders with that library). Until you move one, both lists stay
  alphabetical; anything added later goes at the end.
- **Indexing progress along the bottom.** While Aperture indexes, a bar runs along the bottom of
  the window. It counts the files first ("Counting files in Photos… 12,345 so far"), then shows
  how far it is ("Indexing Photos: 1,234 of 12,345 files"), with a note that it might take a
  while and you can keep browsing. If some of the files it will read are online-only (cloud
  placeholders, such as OneDrive's or Dropbox's), the note says how many: each one is
  downloaded as it's indexed, which is slower.

### Changed
- **The window stays responsive while folders are indexed.** Refreshing the grid as thumbnails
  arrive, a folder's climbing count, and catching up when a run ends now all read the library
  in the background; a large library, or a folder with many subfolders, no longer freezes the
  window on a slower PC. The folder tree is worked out in one pass over the library, rather
  than once for every folder shown.

### Fixed
- **Every library shows its count.** A library you aren't in showed no count until it had been
  open and left; each one's count is now read from its own files when the list is shown.
- A background read could meet a change you were making (removing a folder, saving tags) and
  end Aperture; background reads now never see a change before it's saved. A folder removed
  or hidden while the library was still loading no longer comes back until the next change.

## [0.9.0] - 2026-11-11

### Added
- **Separate libraries.** Keep several independent libraries — say *Work* and *Personal*. They're
  listed under **LIBRARIES** in the folder pane: click one to switch to it, or the open one to see
  everything in it; the header collapses the list. Each library has its own folders, index,
  thumbnail cache, tags, notes, and settings, and never sees another's. Your existing library
  becomes **Default**, with nothing to migrate.
  - **New library…** (the **+** beside LIBRARIES) asks for a name and a folder of photos and
    videos, adds that folder to the new library and starts indexing it. The library's own files
    go in `%LOCALAPPDATA%\Aperture Libraries`.
  - **Open library…** adds a library that already exists (a folder holding `aperture.db`).
  - Every library shows its count: the open one as it changes, the others as they were when last
    open.
  - Right-click a library to **Rename** it. **Remove from list** forgets a library you aren't in;
    its files stay where they are. **Delete library files** deletes Aperture's own files for a
    library you aren't in — its index, thumbnail cache, and settings — and never your photos or
    anything else in its folder.
- **Show or hide subfolders.** Every subfolder in the folder tree has a checkbox, like the
  top-level folders: untick it to hide that folder, and everything inside it, from the library.
- Adding folders — with Add folder, by dropping them on the window, or from the first-run
  panel — asks whether to include their subfolders (Yes unless you say otherwise).
- While Aperture indexes, a card at the bottom of the grid shows what it's indexing and how far it
  has got, and a top-level folder's count in the tree climbs as its photos are added.
- If the library you used last can't be opened (for example, it's on a drive that isn't
  connected yet), Aperture opens another one for now — Default, if it's there — and tells you
  why, instead of failing to start. It tries your library again at the next launch.
- **One window.** Starting Aperture while it's already open brings the open window to the front
  (restored if it was minimized) instead of opening a second one. An update or the installer's
  move still restarts Aperture as before.
- A library that's open in another copy of Aperture (on another PC, for a library on a shared
  drive) can't have its files deleted, and isn't removed from the list while it can be told it's open.
- **Zoom in quick view**, as in Photos and most image editors: `Ctrl`+scroll zooms in and out at
  the pointer, `Ctrl`+`+` and `Ctrl`+`−` zoom about the middle, and `Ctrl`+`0` fits the photo
  again. Zoomed in, drag the photo to look around it. Zooming in loads the photo at full
  resolution (up to 8192 pixels on its longest side), so the detail is there. Another photo opens
  fitted.

### Changed
- `APERTURE_DATA_DIR` now pins Aperture to that one data folder: the library list is ignored and
  no libraries are shown.
- The title bar shows the library's name before the folder's.
- A large library opens several times faster, at launch and when you switch to it, and switching
  back and forth no longer gets slower each time.
- Date sections (day, week, month or year) are chosen to suit the window as well as the photos:
  sparse photos in a wide window are grouped by longer spans, so sections fill their rows instead
  of leaving one photo a row. Resizing the window or zooming can change the grouping.
- A date section you open or close stays that way as the view is rebuilt — when the grouping changes
  and changes back, and in other folders and searches that have it, until you switch libraries or close
  Aperture. Sections you haven't touched follow the default (the newest ~2 screens open) each time.

### Fixed
- Viewing the whole of a library with a single folder (**Everything** before this release; now the
  open library under **LIBRARIES**) showed only the photos directly in that folder, not those in its
  subfolders.

## [0.8.9] - 2026-10-23

### Changed
- **Renamed to Aperture Image Browser** (was "Aperture Image Viewer") — it indexes, browses and
  organizes a local image & video library, which "viewer" undersold. The brand, the `Aperture.exe`
  name, and the data location are unchanged; only the window title and product name differ.

## [0.8.8] - 2026-10-22

First general-availability release. The build is the same as 0.8.8-beta1 (2026-10-19)
with the prerelease suffix dropped; the in-app updater now offers it as a stable release.

### Fixed
- **Transparent images no longer thumbnail as a black tile** — PNG/WebP sources with an
  alpha channel (icons, logos, diagrams) are composited onto white before the JPEG thumbnail
  is encoded. JPEG has no alpha, so transparent pixels were flattening to black and the whole
  tile looked corrupt.
- **Video thumbnails skip a black intro** — a clip that opens on a black fade-in now seeks
  progressively deeper and keeps the brightest frame, so it shows real content instead of a
  black tile. A genuinely dark clip still yields its brightest (dark) frame, which is correct.

### Changed
- **The thumbnail cache size cap is now enforced.** A least-recently-used eviction pass runs
  once per session (off the UI thread), auto-sized to the library so it only trims genuine
  excess and never evicts thumbnails a normal library still needs. Previously the cap was
  configured but never applied, so `thumbs.db` could grow without bound.

### Security
- **System.Security.Cryptography.Pkcs 9.0.21** — the transitive package (via FunkyORM's
  Microsoft.Data.SqlClient) is pinned past GHSA-846f-f764-76mx (high) and
  GHSA-2fmv-rv2c-59x7 (moderate), so restore no longer warns NU1902/NU1903. The running
  app takes Pkcs from the installed .NET Desktop Runtime, and never opens a SQL Server
  connection, so this changes the build audit, not the shipped behaviour.

## [0.8.7-beta1] - 2026-10-14

### Security
- **FunkyORM 3.10.1** — file paths and tag names now reach the database as query
  parameters. FunkyORM 3.9.0 wrote a lookup key into the SQL text, so a file whose
  name contained a quote could read or overwrite another file's tags and note.

### Fixed
- **Install on this PC copied only the exe.** A build whose native libraries
  (`e_sqlite3.dll`, `libSkiaSharp.dll`, `Microsoft.Data.SqlClient.SNI.dll`) sat beside
  `Aperture.exe` was installed without them, and the installed copy closed at
  startup without a word. Install (and the uninstall-time restore) now copy those
  libraries when they sit beside the exe, nothing else from that folder, and refuse
  to install if one can't be copied. Local
  `publish.ps1` builds bundle the natives inside the exe, as the release build
  already did, and fail if any are left loose.
- **Startup errors are shown.** An error before the main window opens now shows a
  dialog with the error chain and the crash-log path instead of closing silently.
- **Files whose path contains an apostrophe** (`Paul's photos`): saving tags or a note
  closed Aperture without saving, and importing tags & notes stopped at the first such
  file. They now save and import normally.
- **Tags that contain an apostrophe** (`mom's birthday`): adding one closed Aperture
  after saving it to the first selected file only, and renaming a tag to one stopped
  after the first file. They now save to every selected file.

## [0.8.6-beta1] - 2026-08-21

### Fixed
- **Folder-tree keyboard** — Tab / Shift+Tab cycle only the folder tree and the tile
  list (ribbon and the Everything button stay out). Tabbing in does not change the
  destination selection. Clicking or Tabbing to the tree gives it keyboard focus so
  arrows move folders immediately. Arrowing the tree commits the selected node at
  once; the tile pane rebuilds in the background and shows the existing Loading…
  spinner if that rebuild is slow, instead of gating the next arrow on grid render.
- **First click** on an unfocused window or pane focuses the clicked folder / tile /
  canvas in that same click (Explorer-style). The activating click is not swallowed.
  The folder expand arrow and include checkbox do not change the selected folder.

## [0.8.5-beta1] - 2026-08-21

### Fixed
- **Tile-pane Up/Down** stay in the same *visual* column at every zoom. 0.8.4
  stepped by a width-based column count that came out one low once the
  scrollbar (and padding) were already excluded from the wrap viewport — a
  4-column grid walked as 3, so selection moved diagonally. The picker now
  uses layout X (and a column count that cannot undercut columns already on
  screen). PageUp/PageDown still move one viewport in that same column;
  Left/Right are unchanged. Headers are not stops.

## [0.8.4-beta1] - 2026-08-21

### Fixed
- **Tile-pane Up/Down** move in the same visual column (Explorer-style), including
  across date-section headers, instead of jumping by linear item index. An empty
  cell (short last row) lands on the nearest tile in that column, then the nearest
  in the row — not a section header and not a random index.
- **PageUp/PageDown** scroll roughly one viewport (regardless of zoom / tile size)
  and keep the selection in the same column at the same screen Y. They no longer
  skip N items or jump to a date-section header.
- **Tab / Shift+Tab** cycle only the folder tree and the tile pane, do not change
  either pane's selection, and skip the ribbon (hamburger / command bar / search).
  Search stays on `/` and Ctrl+F.

## [0.8.3-beta1] - 2026-08-21

### Fixed
- **Clicking the tile-pane canvas** (empty background, not a tile or section header) now
  moves keyboard focus to the contents pane, so arrows, PageUp/PageDown, and scroll keys
  apply to the grid instead of staying on the folder tree.

## [0.8.2-beta1] - 2026-08-21

### Added
- **Installed / portable badge** on the hamburger About — the same AppData-path check the updater already uses (`this process is %LocalAppData%\Programs\Aperture\Aperture.exe`), shown as a quiet caption next to the version.

### Changed
- **Start with Windows from a portable copy** now asks to install first instead of writing `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` to a Downloads (or any portable) path. Declining or cancelling leaves autostart off. After install, the Run value names the AppData exe. Installed copies keep writing and deleting the Run key as before.

## [0.8.1-beta1] - 2026-08-21

### Added
- **About + updater** on the hamburger menu — current version (installed/portable), GitHub
  release check, Update / Update (prerelease) when a newer build is available, progress and
  errors. Running from AppData swaps in place (Midget-style rename dance); a portable copy
  downloads the new exe next to the current one.
- **AppData install** — hamburger **Install Aperture on this PC…** copies to
  `%LocalAppData%\Programs\Aperture\`, adds a Start menu shortcut (optional Desktop), and
  **Uninstall…** reverses it. No `.md` file association — Aperture is not an editor.
- **Start with Windows** — opt-in checkbox on the same Settings surface. Writes or deletes
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Aperture`; off by default. After
  install or update the Run value is rewritten to the AppData exe so logon does not keep a
  Downloads path.

## [0.8.0-beta1] - 2026-07-14

### Added
- **Live indexing feedback** — adding a folder now jumps straight to it and streams its thumbnails in as they're
  indexed, with a determinate progress bar in the status bar (*indexing X / Y*), so a large import never looks frozen.
- **Preview** on the tile right-click menu (top entry, `Space` shortcut) — opens the full-screen quick-look.

### Changed
- **Smoother, steadier grid** — switching views and live re-indexing now reuse existing tiles instead of rebuilding
  every cell, so thumbnails no longer blank-and-reload on each refresh. A background reconcile that finds no changes
  no longer rebuilds the grid at all, removing the re-render flicker that appeared a couple of seconds after launch.

### Fixed
- **`Space` / `Enter`** open the quick-look preview for the selected tile instead of collapsing its date section.
  Collapsing a section stays on the arrow keys, the chevron, and clicking the header.

## [0.7.1-beta1] - 2026-07-11

### Added
- **View → Refresh (F5)** — reloads the library and rebuilds the grid; also the graceful recovery when the
  tile display gets into a wonky state (stale/garbled tiles after alt-tabbing, or a file deleted from under the preview).
- **Context-menu icons** — folder / image / tag / copy / cut glyphs on the tile and preview menus (Windows-standard, on select entries).

### Changed
- **Copy full path** now double-quotes the path only when it contains whitespace, so it pastes cleanly as a
  single shell argument while plain paths stay unquoted.

### Fixed
- `Aperture.Core` retargeted to `net10.0-windows`, clearing the CA1416 platform-compatibility warnings honestly.

## [0.7.0-beta1] - 2026-07-10

First public beta — a fast, local image & video browser for Windows, and a real-world showcase for
[FunkyORM](https://github.com/FuncularLabs/Funcular.FunkyOrm) (its SQLite data access runs entirely on it).

### Highlights
- **Instant browsing** of large, image-heavy folders (a Dropbox *Camera Uploads*, a *Screenshots* dump)
  via a persistent local index + thumbnail cache; the first scan is the only slow one.
- **"Everything" library** (the union of every included folder) plus a folder tree, with a date-grouped,
  virtualized grid that stays smooth at tens of thousands of items.
- **Tags & notes** per file, Gmail-style search (`tag:`, `note:`, `is:video`…), multi-select, a full-screen
  quick-look overlay, and a toggleable inspector pane.
- **Image + video thumbnails** (SkiaSharp + ffmpeg) with EXIF-aware orientation, and **drag-out** of tiles
  as real files onto email, folders, or any drop target.
- Everything stays on your machine — no uploads, no telemetry.

### Notes
- Framework-dependent build; requires the **.NET 10 Desktop Runtime**.
- Signed by Funcular Labs via Azure Trusted Signing (Authenticode).
