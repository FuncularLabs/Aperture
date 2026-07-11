# Changelog

All notable changes to Aperture Image Viewer are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and releases follow [SemVer](https://semver.org/).

## [Unreleased]

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
