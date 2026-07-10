# Publishing to GitHub — 1-2-3

Prep only. **Nothing here has been run** — creating/pushing the repo waits for a go.

Repo target: **`github.com/FuncularLabs/Aperture`** (public, no release/package yet).

## Pre-flight (already done ✓)
- `LICENSE` — MIT (© 2026 Paul Smith — change if it should read "Funcular Labs").
- `README.md` — public README with logo (`docs/logo.png`) + sim-pics screenshot (`docs/screenshot.png`).
- `sample/` — redistributable demo library (free-license + synthetic); `sample/NOTICE.md` credits sources.
- `.gitignore` — excludes `bin/`, `obj/`, `publish/`, `.sample/`, `_recovery/`.
- No secrets in the tree; no personal photos committed (the demo uses `sample/`, not the local `.sample/`).
- Data access dogfoods **FunkyORM** (README + architecture note).

## Do-before-push checklist
- [ ] Decide copyright holder in `LICENSE` (Paul Smith vs Funcular Labs).
- [ ] Confirm the local folder rename, if wanted: `C:\code\Reel` → `C:\code\Aperture` (optional; the remote name is independent of the local folder).
- [ ] Run a clean `dotnet build Aperture.slnx -c Debug` + `dotnet test` (currently green, 87 tests).
- [ ] Adversarial pass over the branch (per house rules) before the push.
- [ ] Remove throwaway `_recovery/` if no longer needed (gitignored, but tidy).

## 1-2-3 (run when given the go)

```powershell
# 1) Create the empty public repo on GitHub (no README/license — we have our own)
gh repo create FuncularLabs/Aperture --public --description "A fast, local image & video browser for Windows — a FunkyORM showcase."

# 2) Point the local repo at it (from C:\code\Reel)
cd C:\code\Reel
git remote add origin https://github.com/FuncularLabs/Aperture.git

# 3) Push main
git push -u origin main
```

Notes:
- Local git history is intact (the Reel→Aperture rename preserved it), so the full commit trail comes along.
- No packages/releases are configured — this is source-only for now.
- The `.git` config user is "Paul Smith"; commits are co-authored with Claude per house convention.
