# Releasing KittyClaw

Versions flow from git tags via MinVer — never edit a csproj version. The
CHANGELOG entry is the single source of the release notes: the GitHub release
body is generated from it.

## Ritual

1. **Changelog** — on `dev`, add a `## [vX.Y] — YYYY-MM-DD` entry at the top of
   `CHANGELOG.md`: a one-line summary, then `### Highlights` prose, then
   `### Added` / `### Changed` / `### Fixed` (and `### Security` when relevant),
   ending with a `---` separator. The one-line summary doubles as the release
   title. Changelog headings intentionally use the short `vX.Y` release line;
   the corresponding MinVer tag includes the patch component (for example,
   heading `v0.13` maps to tag `v0.13.0`). Commit and push.
2. **Merge** — merge `dev` into `main` and push.
3. **Tag** — on `main`:
   ```
   git tag v0.13.0 && git push origin v0.13.0
   ```
4. **Release** — still on `main`:
   ```
   pwsh tools/publish-release.ps1
   ```
   This builds the release zip (Web + QaRunner at the root, ClaudeMock in
   `qa-mock/` — the layout `publish-stable.ps1` uses), verifies the MinVer
   version matches the tag, creates the GitHub release from the CHANGELOG entry,
   and uploads `KittyClaw-v0.13.0.zip` (substituting the version being released).
   Publishing the release is what triggers
   the in-app update banner: running instances poll `releases/latest`.
5. **Local stable instance** (optional) — republish `C:\KittyClaw-stable` with
   `pwsh tools/publish-stable.ps1`, or unzip the release asset there, then
   start `KittyClaw.Web.exe` (defaults to http://localhost:5230).

Afterwards, switch back to `dev` for further work.
