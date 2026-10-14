# Changelog

All notable changes to Aperture Image Viewer are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and releases follow [SemVer](https://semver.org/).

## [Unreleased]

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
