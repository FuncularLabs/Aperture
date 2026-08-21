# Aperture — Roadmap

Planned work, roughly grouped. Ordering is a loose priority, not a schedule or a commitment to
dates. Items are checked off as they ship (each lands a `CHANGELOG.md` entry when it does).

## Distribution & lifecycle

- [x] **AppData install + Start menu** — hamburger Install copies to
  `%LocalAppData%\Programs\Aperture\Aperture.exe`, adds Start-menu (optional Desktop)
  shortcuts, and Uninstall reverses it. In-app update check/apply lives on the hamburger
  About surface: in-place swap when running from that copy, portable apply otherwise.

- [x] **Start with Windows** — opt-in "launch at login" checkbox on the hamburger Settings
  surface (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`). Off by default. Survives
  install/update by rewriting the Run value to the AppData exe.

- [ ] **Tray behavior** — option to close/minimize to the background / system tray instead of
  exiting, so Aperture can stay warm for instant opens. Off by default.

## Tags & metadata

- [ ] **Rename a tag** — rename an existing tag everywhere it's used, ideally under a small
  **Manage tags** surface (rename first; room to grow into merge, delete, and usage counts).

- [ ] **Moved / renamed file detection** — recognize when an indexed file was moved or renamed instead
  of treating it as a delete + a new file, and carry its **tags & notes** across. Let the user control
  how reassociation happens via a preference:
  - **Auto** — silently reassociate when the match is confident.
  - **Batch approve** — collect detected moves and approve them as a group.
  - **One-by-one** — review each proposed reassociation individually.

  A **multi-select list** of proposed matches (accept/reject in bulk) would make batch review far less
  painful than a stream of per-file prompts.

## Copy, cut & paste

- [ ] **Fix multi-select copy/cut** *(bug)* — copying or cutting a multi-file selection currently acts on
  only the **first** item, so a paste transfers just one file. Operate on the whole selection.

- [ ] **Carry tags & notes on paste** *(enhancement, related to the above)* — when a copy/cut → paste moves
  or duplicates files, bring their tags & notes along **by default**, so metadata follows the files instead
  of being left behind.

## Maintenance / chores

- [ ] **Bump GitHub Action majors** — on the next release push, update `actions/checkout`,
  `actions/setup-dotnet`, and `softprops/action-gh-release` off the Node 20-deprecated majors
  (GitHub is currently forcing them onto Node 24).
