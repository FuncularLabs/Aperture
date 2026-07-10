# Publishing to GitHub — 1-2-3

Prep only. **Nothing here has been run** — creating/pushing the repo waits for a go.

Repo target: **`github.com/FuncularLabs/Aperture`** (public, no release/package yet).

## Pre-flight (already done ✓)
- `LICENSE` — MIT, **© 2026 Funcular Labs**.
- `Aperture.App.csproj` — `Version` 0.7.0, `Company`/`Authors`/`Copyright` = Funcular Labs.
- `README.md` — public README with logo (`docs/logo.png`) + sim-pics screenshot (`docs/screenshot.png`).
- `sample/` — redistributable demo library (free-license + synthetic); `sample/NOTICE.md` credits sources.
- `.gitignore` — excludes `bin/`, `obj/`, `publish/`, `.sample/`, `_recovery/`.
- No secrets in the tree; no personal photos committed (the demo uses `sample/`, not the local `.sample/`).
- Data access dogfoods **FunkyORM** — all four metadata tables (roots, items, annotations, tag_stats) run on it; only the BLOB thumbnail cache, schema DDL/PRAGMA, and a couple of lean hot-path projections stay raw (documented in the stores).

## Do-before-push checklist
- [x] Copyright holder set to **Funcular Labs** (LICENSE + csproj).
- [x] Version bumped to **0.7.0** (minor — FunkyORM dogfood + rebrand finish).
- [ ] Rename the local folder `C:\code\Reel` → `C:\code\Aperture` (close any IDE/app holding it first):
      `Rename-Item C:\code\Reel C:\code\Aperture`  — git history is preserved (the `.git` dir moves with it).
- [ ] Run a clean `dotnet build Aperture.slnx -c Debug` + `dotnet test` (currently green, 87 tests).
- [ ] Adversarial pass over the branch (per house rules) before the push.
- [ ] Remove throwaway `_recovery/` if no longer needed (gitignored, but tidy).

## Code signing (Authenticode) — reuse the Funcular Labs Trusted Signing account
Aperture reuses the **same** Azure Trusted Signing setup as Markdown Midget — Trusted Signing has no
exportable per-product key; one **certificate profile** carries the *publisher* identity (Funcular Labs)
and signs any number of that publisher's products. Nothing new needs to be provisioned to sign Aperture.

- Account: `func-az-artifact-signing` · Profile: `funcular-labs-public-trust` · Endpoint: `https://cus.codesigning.azure.net/`
- Tool: `azuretrustedsigntool` (dotnet global tool). Auth: the existing service principal via
  `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET`. That SP already holds the
  *Trusted Signing Certificate Profile Signer* role, so **no RBAC change** is required — the only per-app
  change is `--description "Aperture Image Viewer"` (and `--file`).
- **Add the three SP secrets to the Aperture repo** (or, better, promote them to **FuncularLabs org-level
  secrets** so every Funcular app shares one set).
- **Resource tag:** the account currently carries `Product: MarkdownMidget`, which is now misleading since
  it's a shared publisher signing resource. Retag to reflect that — e.g. drop `Product` and add
  `Purpose: code-signing`, `Scope: funcular-labs-shared`. Tags are inventory-only; they do **not** gate
  which products can be signed, so this is cosmetic/accuracy, not a functional requirement.
- The Aperture release workflow will mirror Markdown Midget's `.github/workflows/release.yml` minus the
  Node/editor-bundle steps (Aperture has no JS editor). Deferred to the CI/release step below.

## 1-2-3 (run when given the go)

```powershell
# 1) Create the empty public repo on GitHub (no README/license — we have our own)
gh repo create FuncularLabs/Aperture --public --description "A fast, local image & video browser for Windows — a FunkyORM showcase."

# 2) Point the local repo at it (from C:\code\Aperture, after the folder rename above)
cd C:\code\Aperture
git remote add origin https://github.com/FuncularLabs/Aperture.git

# 3) Push main
git push -u origin main
```

Notes:
- Local git history is intact (the Reel→Aperture rebrand preserved it), so the full commit trail comes along.
- No packages/releases are configured — this is source-only for now.
- The `.git` config user is "Paul Smith"; commits are co-authored with Claude per house convention.
