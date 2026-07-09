# Aperture

A fast image browser for Windows. Replaces File Explorer for the "just let me look at my photos" case — no 30-second waits on big folders like Dropbox Camera Uploads.

(Formerly "Reel". The on-disk data store is still `%LOCALAPPDATA%\Reel` so the existing index survives the rename.)

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

### Video thumbnails ✅ done
Added on top of M2. Video files (`.mp4/.mov/.mkv/.webm/...`) are indexed and thumbnailed, on by default (toggle in Settings; toggling re-indexes). Video tiles carry a play badge; a `kind` column (with migration) distinguishes image/video. Thumbnail source, in order:
1. **A real decoded frame via ffmpeg** (`VideoFrameExtractor`) — ffmpeg carries its own codecs, so this produces frames even for files the Windows shell won't thumbnail (e.g. H.264 clips where VLC took over the association without providing a thumbnail handler — the common Android-via-Dropbox case). ffmpeg is found via `REEL_FFMPEG`, then PATH, then a copy next to the app.
2. **The Windows shell** (`IShellItemImageFactory`, STA thread) when ffmpeg is unavailable.
3. **The file-type icon** as a last resort, so a video tile is never blank.

Verified: an Android H.264 clip that shows the VLC cone in Explorer renders its real frame in Reel. **Note:** step 1 needs an ffmpeg binary present; bundling one with the app (or `winget install ffmpeg`) makes video frames work on any machine. Deferred: bundle ffmpeg in the publish output; smarter frame selection (skip near-black opening frames).

### M3 — Sections, sort, captions ✅ done
- Collapsible date sections via `CollectionView` grouping + grouped `VirtualizingWrapPanel`. Header shows label + subtotal + chevron; click or `Space` toggles. Smart default collapse expands the newest ~2 screens; older sections collapsed. Bucket granularity (week/month/year) chosen from item density and **re-chosen on the filtered set**.
- Tokenized caption format (`{date:fmt}`, `{alias}`, `{size}`, `{dim}`, `{camera}`, `??` fallback), editable in Settings.
- Multi-level sort engine (`SortSectioned`: date-section primary, user sort within); exposed via presets (newest/oldest/name/largest/camera).
- Right-click tile → **hide its folder** from the view (persisted exclusions, "Show all" reset).
- Filter box, `/` to focus, live over name/alias/camera.
- Verified on 103 photos spanning 2013–2026: year sections with older auto-collapsed, click-collapse, filter → re-bucketed to month.

**Deferred/refinement:** full sort token-picker editor (presets ship instead); folder-tree exclude UI (tile-based hide ships instead); `Ctrl+→/←` section jump (Space toggle ships).

### M4 — Polish for daily-driver status ✅ done
- **First-run welcome**: auto-detects common photo folders (Pictures, OneDrive Pictures/Camera Roll, Dropbox Camera Uploads, iCloud Photos, Screenshots) and offers them with checkboxes.
- **Quick-look overlay** (`Space`): full-res image on a dim backdrop, `←/→` to move, `Esc`/click to close; videos show their large thumbnail.
- Root **alias rename** (double-click or right-click → Rename), persisted; captions update.
- **Settings pane** (popup): video toggle, date grouping, caption format, sort presets, hidden-folder reset.
- **Persistence**: window size/position/maximized and zoom restored across runs (`settings.json`). `REEL_DATA_DIR` env var relocates the store.
- `publish.ps1` — framework-dependent (default) or `-SelfContained` single-file publish.

**Deferred/refinement:** color tags, section-expansion persistence, per-root zoom, cache-cap/thumb-size settings, MSIX packaging.

### Refinements (post-M4) ✅ done
- **Folder navigation.** Subdirectories show as folder tiles (a "Folders" section) instead of being flattened; double-click drills in, breadcrumb + ← / ↑ / Backspace go back. Home is the single root's top (or root tiles with multiple roots). Indexing stays recursive; **search** (`/`) does a flat recursive result under the current folder (Explorer-style). Date sections apply to the current folder's direct media.
- **Square-fill thumbnails** (center-crop) for a clean uniform grid; quick-look still shows the whole image.
- **Real video frames** chosen with ffmpeg's `thumbnail` filter (skips black/transitional frames).
- **Context menu**: Copy image / Copy thumbnail, Copy file name, Copy full path, Copy + Cut as file-clipboard ops (Explorer paste/move), Hide folder. `Ctrl+C`/`Ctrl+X` on the selection.
- **Zoom anchors** to the item near the viewport centre.
- **Arrow-key navigation** (Explorer-style: ←/→ item, ↑/↓ row) with scroll-into-view.
- **~12 date sections** target — granularity (day/week/month/year) picked so the section count lands near 12.

### Feedback round 2 ✅ done
- **Sort is an always-visible toolbar dropdown** showing the active sort. Date sorts (Newest/Oldest) keep collapsible date sections; every other sort (Name, Largest, Camera…) **flattens to one global list** so the order is obvious.
- **Scope = root checkboxes only.** Removed the per-tile "Hide this folder" exclusions (folder navigation replaces them).
- **Coherent grouped keyboard navigation.** A cursor walks section headers *and* visible tiles: arrows move predictably (Up/Down stop on the next header rather than skipping into a section), the header highlights when the cursor is on it, `Enter`/`Space` toggles a section, `Enter` opens/enters a tile. No more sections auto-expanding as you arrow through.

### Feedback round 3 ✅ done
- **App icon + mascot + version.** A film-reel glyph on a purple gradient (generated multi-size `.ico`); set as the window/exe icon; version `0.5.0`; the Settings popup shows the mascot + "Reel v0.5.0".
- **"Open with" context submenu.** Lists the shell handlers registered for the file's extension (`SHAssocEnumHandlers`) and invokes the chosen one (`IShellItem.BindToHandler` → `IDataObject` → `IAssocHandler.Invoke`), plus "Choose another app…" (`OpenAs_RunDLL`).
- **Photos Back/Forward.** Media opens through `explorer.exe` so the default viewer gets the file's folder context and can page through siblings, like an Explorer double-click.
- **Folder tree in the left nav.** Subfolders moved from top grid tiles into a left `TreeView` (roots → lazy subfolders, with counts + include checkboxes on roots, rename/remove on the root context menu). Selecting a node shows that folder's direct media; the grid + breadcrumb clutter is gone and the nav is narrower. Search still spans the subtree.

**Deferred/refinement:** nav width is a fixed narrower value with a splitter (not yet auto-fit to the longest entry); the tree rebuilds (collapsing other branches, current path restored) when indexing completes; folder-preview thumbnails on nodes.

### Feedback round 4 ✅ done
- **Branding**: larger logo + "Reel" wordmark in the toolbar; the window title shows the current folder's full path.
- **Tree order**: subfolders sort name-**descending** by default (year folders newest-first).
- **Tags & Notes** (`Ctrl+T` or right-click → "Tags & notes…"): one dialog with a tag chip editor (type to add, pick from suggestions, ✕ to remove — new tags grow the suggestion list) and a free-text note. Stored keyed by path in `reel.db` (survive re-index / root re-add). Annotated tiles show a 🏷 badge; tooltip shows tags + note.
- **Gmail-style search**: `tag:x`, `note:x`, `has:tag`/`has:note`, `is:video`/`is:image`/`is:tagged`, `type:mp4`, `name:`, `camera:`, `folder:`, quoted values (`tag:"date night"`); bare words match name/alias/camera/tags/note; terms are AND-ed. Search is now global across the library. Core-tested.

### Feedback round 5 ✅ done
- **Context-menu dialog fix**: the tile right-click → "Tags & notes…" now actually launches. Root cause was that `RelativeSource AncestorType=Window` doesn't resolve inside a `ContextMenu` (a separate popup tree); commands are now routed through a `BindingProxy` held in the window's resources (`Source={StaticResource Vm}`), which fixes every context-menu command.
- **Tag management UI** (Settings → "Manage tags…"): lists every tag with a per-file count; edit a name + Enter to rename (renaming onto an existing tag merges them); Delete removes a tag from all files (and drops now-empty annotation rows). Core-tested (counts, rename, merge, delete).
- **`Ctrl+F`** focuses the search box (as does `/`); the box is wider.
- **README from Settings**: "View README (help)" opens this file; it's copied next to the app on build.
- **Tooltip location**: since search spans subfolders, each tile's tooltip shows its `FOLDERS`-relative path, e.g. `in [Camera Uploads/2014]`.
- **Open like a double-click**: files now shell-execute their default `open` verb with the containing folder as the working directory (instead of via `explorer.exe`) — the closest replica of an Explorer double-click, which is what gives folder-aware viewers their context. Note: whether Windows Photos pages siblings with its Back/Forward arrows is decided by Photos itself once it has the file + folder; Reel can only hand it off faithfully.

**Deferred/refinement:** note markdown; sidecar export of annotations (deferred at the user's request).

### Feedback round 6 ✅ done
- **"Copy image" / quick-look orientation**: rotated phone photos (EXIF orientation ≠ 1) were copied/previewed sideways because WPF's `BitmapImage` ignores EXIF orientation. Both paths now decode through SkiaSharp's orientation logic — the *same* `SKCodec.EncodedOrigin` + matrix code the thumbnails use — so a copied image is byte-for-byte the same orientation as the tile and Windows Photos. Verified end-to-end (the reported file copies as 2252×4000 portrait, not 4000×2252 landscape). New Core tests cover the axis-swap, the no-orientation case, and the longest-edge cap.

### Feedback round 7 ✅ done
- **Tagging no longer disturbs the grid**: editing tags/notes (and bulk tag-manager changes) update the affected tiles' badges/tooltips **in place** instead of rebuilding the view. The scroll position, selection, and tile sizing stay exactly as they were — previously the grid rebuilt, which reset scroll (sometimes to the bottom) and briefly mis-sized thumbnails.
- **Multi-select** (Explorer-style: Ctrl+click to toggle, Shift+click for a range). Right-clicking an unselected tile selects just it; right-clicking within a selection keeps it.
- **Tag/note on many items at once** (`Ctrl+T` or right-click → "Tags & notes…" with several selected):
  - **Tags** show as an aggregate. A tag on *every* selected item is solid; a tag on *some* shows a **dashed outline** with a "Pertains to K of N selected" tooltip. **Adding** a tag applies it to all; the **✕** removes it from all that have it.
  - **Notes**: the field shows the majority note; saving applies it to every item that has that note **or no note**. Items carrying a *different* note are called out ("N selected items have different notes — they won't be changed") and left untouched, so a bulk note edit never clobbers a distinct note.
- Multi-item merge rules live in `Reel.Core.Annotations.AnnotationMerge` and are unit-tested (add/remove/case-folding, note majority + exclusion).

### Feedback round 8 ✅ done
- **Tags ordered by recency of use.** A per-tag last-used timestamp (`tag_stats`) is bumped whenever a tag is *added* to an item (never on removal). Tag lists — dialog chips + suggestions and the tag manager — now sort most-recently-used first. Recency is a per-tag (lookup-level) fact, not per (item, tag). Existing libraries are backfilled from current usage.
- **Medium-dark canvas.** The left folder pane and the tile grid use a medium-dark theme (approaching VS dark, a touch lighter — `#22252B` / `#282B32`), with light text, legible captions/section headers, and dark-appropriate selection/hover. The toolbar, status bar, and dialogs stay light.
- **Folder tree expanded by default** — every node is expanded on load (and after background rebuilds).
- **Search quick-picks.** Focusing the search box pops up your top tags. When usage is highly skewed (a few tags dominate) it offers the **most-used**; otherwise it offers the **most-recent** — a distribution the code decides ([`TagQuickPicks`](src/Reel.Core/Annotations/TagQuickPicks.cs)). Clicking one adds a `tag:` clause.
- **`tag:` search is OR.** Multiple `tag:` clauses match items with *any* of them (e.g. `tag:beach tag:city`), still AND-ed with non-tag terms.

### Feedback round 9 ✅ done
- **Snappy folder navigation.** Selecting folders in the left tree (keyboard or mouse) no longer buffers/replays keystrokes. Two causes fixed: (1) the grid rebuild used to re-read the whole item union from SQLite on *every* selection — now the union is cached and only re-read when the data actually changes (index, root add/remove/include); (2) rebuilds are now **debounced (last-one-wins) and run off the UI thread**, so rapid arrowing coalesces into one rebuild and never blocks input. A brief "Loading…" spinner covers the gap. The heavy filter/sort/tile work is computed on a background thread against immutable snapshots (fresh, unbound `SectionVm`s), then applied on the UI thread.

### Feedback round 10 ✅ done — rebrand + inspector
- **Renamed to Aperture** with a new aperture-iris logo (purple metallic blades + motion lines). The data directory stays `%LOCALAPPDATA%\Reel` so the existing index/annotations carry over untouched.
- **Preview / inspector pane** (toolbar "◧ Preview" toggle): shows the selected item large with **zoom** (wheel or −/Fit/1:1/+) and **pan** (drag), its **tags & notes** (with an inline "Edit tags & notes…" that opens the editor), and full **metadata/EXIF** — dimensions, size, dates, plus camera/lens/exposure/ISO/GPS read on demand ([`MetadataReader.ReadExifSummary`](src/Reel.Core/Media/MetadataReader.cs)).
- **Open containing folder** on the tile context menu (reveals the file in Explorer).
- **Sync-status pill** in the top bar: "N items · up to date", or the live "Indexing…" progress.
- **Drag-and-drop folders** onto the window to add them as watched roots.

**Evaluated from the review notes — deferred (with reasons):** *Smart Albums / Recents / Favorites* sidebar sections (valuable, but Favorites needs a new persistent "starred" flag and Smart Albums want a saved-search model — worth a dedicated pass); *smooth tree expand/collapse animation* (WPF `TreeView` has no native animated expansion — low ROI vs. effort); *kebab menu for secondary actions* (the toolbar isn't crowded enough yet to warrant hiding actions).

### Feedback round 11 ✅ done — nav + UI polish
- **Keyboard expand/collapse**: in the folder tree, Space toggles the selected node (Right/Left already expand/collapse); in the grid, when the cursor is on a date-section header, Right expands / Left collapses it (Space still toggles).
- **Show: Pictures / Videos** checkboxes in the top bar — a live filter over the current view.
- **Grid starts at the top on navigation**, and remembers each folder's scroll position so returning to a previously-viewed folder restores where you were (per-location scroll memory).
- **Sort dropdown** text is vertically centered (was top-aligned).
- **Clicking outside the search box dismisses** the tag quick-picks.
- **Tag chooser**: on a multi-selection, a tag that's on only *some* items now stays in the available/suggestions list (so you can one-click apply it to all), in addition to showing as a dashed "partial" chip.
- **Settings → ☰ hamburger** (it'll host more than settings soon).

### Feedback round 12 ✅ done
- **Teal folder icons** in the tree (a vector folder glyph that reads clearly on the dark charcoal and complements the purple logo), replacing the near-invisible black emoji.
- **Keyboard focus follows the scroll**: Home/End (and Ctrl+Home/End) move the grid cursor to the first/last item, and if the cursor was scrolled out of view (wheel, scrollbar, Ctrl+End) the next arrow re-anchors to a visible tile instead of jumping back.
- **Horizontal scrollbar** appears when grid content is clipped on the right (was disabled).
- **Preview pane can dock right, bottom, or off** — the toolbar button cycles through the three; the tags & notes band is more prominent (larger text on a lighter gray rectangle).

### Feedback round 13 ✅ done
- **Hyphenated tags**: multi-word tags are normalized to hyphens (`date night` → `date-night`) on entry, on save, and for `tag:` search values ([`TagNormalizer`](src/Reel.Core/Annotations/TagNormalizer.cs)). Existing libraries are migrated once (guarded by `PRAGMA user_version`).
- **Grid reflows on preview open** (the horizontal scrollbar was making tiles unreachable) — the thumbnail wrap panel now re-columns to the narrower width when the right preview opens, so nothing is clipped.
- **Bottom preview uses the margins**: tags/notes on the left, image + zoom in the center, metadata on the right (right dock stays stacked). One set of controls, repositioned by a `PreviewModeConverter`-driven grid.
- **Removable tags in the preview** (a ✕ on each chip) and **correct zoom-button glyphs** (− / + were rendering blank).

### Feedback round 14 ✅ done
- **Preview pane is a real, resizable split** now: the browser grid and the pane are genuine grid cells with a **`GridSplitter`** between them (drag the pane's left edge when docked right, top edge when docked bottom). Opening/closing/resizing the pane reflows the thumbnail wrap panel and resets scrollbars exactly like a window resize — no more clipped, unreachable tiles on the right/bottom. (Configured in `ConfigurePreviewLayout`.)
- **Keyboard nav scrolls collapsed section titles into view**: Home/End scroll to the very top/bottom, and landing on a section header (even a collapsed, item-less one) brings that header into view (`BringHeaderIntoView`). Previously Ctrl+End could move the cursor to the last collapsed title without moving the scroll position.

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

# Build a distributable exe (needs .NET 10 Desktop Runtime on the target):
pwsh ./publish.ps1                 # framework-dependent
pwsh ./publish.ps1 -SelfContained  # bundles the runtime
```

Data lives in `%LOCALAPPDATA%\Reel\` (`reel.db`, `thumbs.db`, `settings.json`). Delete that folder to reset. Set `REEL_DATA_DIR` to relocate it.

## Status

M1–M4 complete plus video thumbnails and eight rounds of feedback. 74 xUnit tests over the Core engine (indexer, thumbnails, orientation, watcher, union, formatting, search, annotations, tag management, multi-item merge, tag recency, quick-pick distribution). The WPF app has been verified end-to-end against real photo/video libraries (grid, sections, collapse, filter, zoom, quick-look, first-run, settings, tags & notes, tag manager, search, copy-image orientation). See each milestone above for what shipped and what was deferred.
