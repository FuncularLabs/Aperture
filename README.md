# Reel

A fast image browser for Windows. Replaces File Explorer for the "just let me look at my photos" case — no 30-second waits on big folders like Dropbox Camera Uploads.

Working name; easy to rename later.

---

## Why

Explorer is slow on image-heavy folders because it enumerates + probes shell extensions + re-extracts thumbnails on every cold view. Reel wins by keeping a **persistent local index + thumbnail cache** and rendering from that. First scan is slow; every subsequent open is instant.

## Product principles

1. **Fast open, always.** Cold open of any indexed root ≤ 500 ms to first paint.
2. **Union across roots.** Include multiple folders (from anywhere on disk) into one date-sorted view.
3. **Keyboard-first, mouse-friendly.** No modal dialogs on the hot paths.
4. **KISS core, extensible caption/sort.** Format strings with tokens instead of a settings jungle.

Explicit non-goals for v1: edit, rate, tag, face detection, cloud sync, video transcoding, cross-platform.

---

## Feature spec

### Roots & union view
- Left nav shows only user-added roots (no system tree).
- Each root has a user-assigned **alias** (resolves collisions where multiple folders are called "Pics").
- Per-root checkbox = include in union.
- Right-click any subfolder inside a root → **exclude from union** (surgical carve-outs stored per root).
- Add roots via toolbar `+`, menu, or drag-and-drop a folder onto the pane.
- Roots can carry a color tag (optional visual cue in captions/borders).

### Index & cache
- On add: background scan → per-root SQLite file (`<root>/.reel/index.sqlite`, or centralized in `%LOCALAPPDATA%\Reel\` if the root is read-only).
- Columns: `path`, `size`, `mtime`, `exif_date`, `width`, `height`, `camera`, `orientation`, `hash?`.
- Thumbnails cached at 3 sizes (`sm=128`, `md=256`, `lg=512`) as BLOBs in the same DB. LRU eviction past a configurable cap (default 2 GB total).
- `FileSystemWatcher` per root, 500 ms debounce, incremental re-index.
- First-run: auto-detect `%USERPROFILE%\Pictures`, Dropbox `Camera Uploads`, iCloud Photos folder, and offer to add.

### Browse surface
- Virtualized grid (WPF `VirtualizingStackPanel` + `ItemsControl` with recycling).
- Zoom levels (5): **Details** → **Small tile** (96) → **Medium tile** (160) → **Large tile** (256) → **XL tile** (400).
- `Ctrl+wheel` / `Ctrl++` / `Ctrl+-` steps zoom. Persisted per root and per section-mode.
- **Collapsible date sections** with subtotal in header: `▼ June 2026 (148)`.
- Smart default collapse:
  - Compute median items/week across the union.
  - `>= 40/week` → week buckets, `>= 8/week` → month buckets, else → year buckets.
  - Auto-collapse everything older than the current bucket; opening the app shows ≤ 2 screen-heights.
- Keyboard: arrows, `PageUp`/`PageDn`, `Home`/`End`, `Ctrl+→`/`Ctrl+←` to jump section boundaries, `Space` to expand/collapse the current section, `/` to open the filter box, `Enter` opens in the OS default app.

### Captions (tokenized)
- Per-zoom-level format string.
- Tokens: `{alias}`, `{name}`, `{ext}`, `{size}`, `{mtime:fmt}`, `{exif.date:fmt}`, `{exif.camera}`, `{dim}`, `{w}x{h}`.
- Default caption: `{exif.date ?? mtime:yyyy-MM-dd HH.mm} · {alias}`.

### Sort (tokenized, multi-level)
- Sort spec is an ordered list of `{token, direction}` pairs.
- Default: `[{exif.date ?? mtime, desc}, {alias, asc}]`.
- Saveable presets.

### Status bar
- Left: total items in current view.
- Center: selected count · selected size.
- Right: indexing progress ("Indexing 1,204 / 8,900 · Camera Uploads") + eviction/cache state when active.

### Out-of-scope for v1
- Built-in image editor.
- Full-screen viewer with pan/zoom beyond a lightweight `Space` quick-look (deferred to v1.1).
- Rating / tag / face detection.
- Cloud storage direct integrations (works via mounted folders).

---

## Architecture

- **`Reel.Core`** — indexer, watcher, thumbnail pipeline, SQLite schema, models. No WPF references. Testable in isolation.
- **`Reel.App`** — WPF UI, ViewModels, virtualization, hotkeys, settings persistence.
- **`Reel.Core.Tests`** — xUnit tests around the indexer and token/caption/sort engine.

Thumbnail decode: SkiaSharp for JPEG/PNG/HEIC/WEBP. Windows `IThumbnailProvider` fallback for exotic formats.

EXIF: MetadataExtractor lib.

Storage: raw `Microsoft.Data.Sqlite` (KISS). Can swap to FunkyORM later.

---

## Milestones

Small, working slice at each milestone. Each ends with an app you can actually use — increasing capability, not layers of scaffolding.

### M1 — Indexer & thumbnail cache ✅ done
The engine, headless.
- Root model + SQLite schema. **Build decision:** one central store (`%LOCALAPPDATA%\Reel\`) with a `root_id` column rather than a DB file per root — union queries are the core feature and are trivial against a single table. Metadata (`reel.db`) and thumbnail BLOBs (`thumbs.db`) are split into two files so BLOBs don't pollute the metadata page cache.
- Background scanner: fault-tolerant recursive enumerate, extract EXIF (capture date, camera, orientation), capture size + mtime, upsert.
- Thumbnail pipeline: single SkiaSharp decode → orientation-corrected → resize to 3 sizes (128/256/512) → store JPEG BLOB.
- `FileSystemWatcher` with 500 ms debounce coalescing bursts into one re-index trigger.
- Incremental reconcile: unchanged (size+mtime match) → skip with no decode; changed → re-index; deleted → prune rows **and** orphaned thumbs; partial cache → self-heal.
- 22 xUnit tests cover schema round-trip, indexing, resume-skips-unchanged (no decoding), new/removed/modified detection, self-heal, thumbnail sizing/aspect/orientation, scanner filtering, watcher coalescing.
- Exit criteria: resume over an already-indexed tree does zero decoding (verified: `Skipped == count`, `ThumbnailsGenerated == 0`). Real-folder wall-clock benchmark lands with the M2 UI wiring.

### M2 — Union grid (browse surface) ✅ done
The MVP UI. WPF + a thin MVVM (no framework), `LibraryService` as the single UI-facing facade.
- Left nav with roots list + include checkbox + add/remove (folder picker via `Microsoft.Win32.OpenFolderDialog`), per-root count + live indexing status.
- Main pane: virtualized grid (`VirtualizingWrapPanel`, recycling) bound to the union of included roots.
- Five zoom stops (96/140/200/280/400) via `Ctrl+wheel` and `Ctrl+±`.
- Sort hardcoded to EXIF-date-desc, then alias (M3 makes it configurable).
- Status bar with total count + indexing progress.
- `Enter` / double-click opens in the OS default app.
- On launch, cached rows paint instantly; each root then reconciles with disk in the background (first index streams results in), and a per-root `FileSystemWatcher` triggers incremental re-index on changes.
- Verified against 40 real Camera Uploads photos: grid renders with correct aspect/orientation, captions, sort; zoom re-wraps live; resume re-index = 3 ms.

**Build decisions / known limitations (M2):**
- **Thumbnail loading** is driven by the `Thumbnail` binding lazy-loading on realization (decode off-thread → frozen `BitmapSource`), *not* container lifecycle events — WPF fires `DataContextChanged`/`Unloaded` unreliably under `VirtualizingWrapPanel` (confirmed: `DataContextChanged` never fired).
- M2 always decodes the **Large (512)** thumbnail and scales in the view; per-zoom thumbnail sizing is an M3 optimization.
- **Memory:** a realized `TileVm` retains its decoded bitmap, so scrolling a very large library (10k+) grows memory beyond the thumbnail cache's cap. Fine for the ~2k Camera Uploads target; viewport-based release is an M3 task.
- **Details (columns) list** view and scroll-position-preserving incremental grid updates are deferred to M3.
- Exit criteria: add Camera Uploads and browse the union; cold re-index resumes from cache in milliseconds. ✅

### M3 — Sections, sort, captions
Turn it into the tool you actually want.
- Collapsible date sections with subtotals and smart default collapse.
- Section keyboard nav (`Ctrl+→/←`, `Space`).
- Tokenized caption format string, editable per zoom level.
- Multi-level sort UI with token pickers.
- Right-click subfolder → exclude from union.
- Filter box (`/`).
- Exit criteria: Can browse a 50k-item union, jump by year, and switch caption format without a restart.

### M4 — Polish for daily-driver status
The "ditch File Explorer" milestone.
- First-run wizard with auto-detection of common photo folders.
- Root aliases (rename on demand), color tags.
- Sort/caption presets, saved and switchable.
- Settings pane for cache cap, thumb sizes, default zoom.
- Persist window layout, per-root zoom, section expansion state.
- Optional: `Space` quick-look overlay (single-image, arrows to navigate).
- Optional: MSIX packaging or single-file `dotnet publish` script.

### Later (post-v1, if warranted)
- Face grouping (opt-in, local models).
- Ratings/tags with sidecar `.reel.json` or extended attributes.
- Cross-platform port via Avalonia.
- FunkyORM as the storage layer.

---

## Repo layout

```
Reel.sln
src/
  Reel.Core/    class lib, net10.0
  Reel.App/     WPF app,  net10.0-windows
tests/
  Reel.Core.Tests/  xUnit, net10.0
```

## Getting started

```powershell
dotnet build
dotnet test
dotnet run --project src\Reel.App
```
