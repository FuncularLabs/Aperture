# Aperture — Roadmap

Planned work, roughly grouped. Ordering is a loose priority, not a schedule or a commitment to
dates. Items are checked off as they ship (each lands a `CHANGELOG.md` entry when it does).

## Distribution & lifecycle

- [ ] **Real installer & updater** — a proper Windows installer (Start-menu entry, clean uninstall,
  file associations where they make sense) to replace the bare framework-dependent single-file `.exe`,
  plus an in-app update check/apply so test users don't have to re-download by hand each beta.

- [ ] **Start with Windows + tray behavior** — an opt-in "launch at login" setting, and an option to
  close/minimize to the background / system tray instead of exiting, so Aperture can stay warm for
  instant opens. Both **off by default**.

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

## Maintenance / chores

- [ ] **Bump GitHub Action majors** — on the next release push, update `actions/checkout`,
  `actions/setup-dotnet`, and `softprops/action-gh-release` off the Node 20-deprecated majors
  (GitHub is currently forcing them onto Node 24).
