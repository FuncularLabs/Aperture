# Publishing to GitHub

Repo: **`github.com/FuncularLabs/Aperture`** — **private** for now (flip to public when ready).
CI (`.github/workflows/ci.yml`) and a signed release pipeline (`release.yml`) are in the tree.

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
- [x] Version set to **0.7.0-beta1** (first public beta).
- [x] CI + signed release workflows added.
- [ ] Rename the local folder `C:\code\Reel` → `C:\code\Aperture` (close any IDE/app holding it first):
      `Rename-Item C:\code\Reel C:\code\Aperture`  — git history is preserved (the `.git` dir moves with it).
- [ ] Add the three `AZURE_*` signing secrets (org-level or on the repo) so the release job can sign — see below.

## Code signing (Authenticode) — reuse the Funcular Labs Trusted Signing account
Aperture reuses the **same** Azure Trusted Signing setup as Markdown Midget — Trusted Signing has no
exportable per-product key; one **certificate profile** carries the *publisher* identity (Funcular Labs)
and signs any number of that publisher's products. Nothing new needs to be provisioned to sign Aperture.

- Account: `func-az-artifact-signing` · Profile: `funcular-labs-public-trust` · Endpoint: `https://cus.codesigning.azure.net/`
- Tool: `azuretrustedsigntool` (dotnet global tool). Auth: the existing service principal via
  `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET`. That SP already holds the
  *Trusted Signing Certificate Profile Signer* role, so **no RBAC change** is required — the only per-app
  change is `--description "Aperture Image Viewer"` (and `--file`).
- The `AZURE_*` secrets live at the **FuncularLabs org level** (Markdown Midget uses the same set). They're
  scoped to **Selected repositories**, so a *new* repo doesn't get them automatically — **add Aperture** to
  each secret's repository access (Org → Settings → Secrets and variables → Actions), or the release job's
  fail-fast check stops it. If the secrets aren't scoped in, `azuretrustedsigntool` hangs on
  "Submitting digest for signing…" (it silently falls back to absent Azure CLI creds).
- **Resource tag:** the account currently carries `Product: MarkdownMidget`, which is now misleading since
  it's a shared publisher signing resource. Retag to reflect that — e.g. drop `Product` and add
  `Purpose: code-signing`, `Scope: funcular-labs-shared`. Tags are inventory-only; they do **not** gate
  which products can be signed, so this is cosmetic/accuracy, not a functional requirement.
- The release workflow (`.github/workflows/release.yml`) mirrors Markdown Midget's, minus the Node/editor
  steps: it publishes a framework-dependent single-file `Aperture.exe`, signs it with `--description
  "Aperture Image Viewer"`, and attaches it to a GitHub Release built from `CHANGELOG.md`.

## Cutting a release
1. Ensure `CHANGELOG.md` has a `## [<version>]` section for the version.
2. Tag and push: `git tag v0.7.0-beta1 && cd C:\code\Reel && git push origin v0.7.0-beta1`.
3. The `Release` workflow builds → tests → publishes → **signs** → creates the (pre)release with the exe.
   (`-beta`/`-rc` tags are marked as prereleases automatically.)

Notes:
- Local git history is intact (the Reel→Aperture rebrand preserved it), so the full commit trail comes along.
- The `.git` config user is "Paul Smith"; commits are co-authored with Claude per house convention.
