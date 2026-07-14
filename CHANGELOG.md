# Changelog

All notable changes to Aperture Image Viewer are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and releases follow [SemVer](https://semver.org/).

## [Unreleased]

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
