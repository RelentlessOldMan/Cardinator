# Cardinator — working rules for this repo

## Versions and releases (the procedure)

- `<Version>` in `src/Cardinator/Cardinator.csproj` is the single source of truth; the app reads it at
  runtime (`App.VersionNumber()`). Never hardcode a version anywhere else.
- **Every batch of work that gets pushed bumps the PATCH** (1.6.0 → 1.6.1 → 1.6.2 …).
- **The MINOR is bumped only for big work, and only after the user agrees to that specific bump.**
- **Never bump the MAJOR (2.0). That is the user's decision alone.**
- **Every version pushed to `main` gets a GitHub release.** This is automatic: the `release` job in
  `.github/workflows/ci.yml` runs after the tests pass, and if `v<Version>` has no release yet it
  publishes the single self-contained `Cardinator.exe` and creates the release (notes = the pushed
  commit's message). So: bump the version → commit → push → the release appears. After pushing, check
  the CI run went green and the release exists (`gh release view v<Version>`).
- If the automatic release ever fails, release by hand from the same commit (see
  `docs/DEVELOPING.md` → "Releasing"). Never move or reuse an existing tag — fix forward with a new
  patch version instead.

## Data safety

- New versions must open every older file format (additive, optional fields with defaults only;
  `JsonCompat`, `LegacyFixtureTests`, golden tests guard it).
- Saves keep rolling backups (`ProjectBackup`); never weaken atomic writes or the backup step.

## Repo hygiene

- `jacob/` (Jacob's personal FMA frames and their build scripts) is gitignored and must never be
  committed or shipped. The shipped sample frames are the sanitized "Partial Cardboard Chemist" set.
- Long commit messages: write `commit-msg.tmp` in the repo root, `git commit -F commit-msg.tmp`, then
  delete it. Never write anything under `.git\`.
- Review renders for the user go in `C:\temp\fma_jacob\REVIEW`.
