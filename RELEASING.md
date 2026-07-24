# Releasing KittyClaw

Versions flow from git tags via MinVer — never edit a csproj version. The
CHANGELOG entry is the single source of the release notes: the GitHub release
body is generated from it.

## Ritual

1. **Changelog** — on `dev`, add a `## [vX.Y] — YYYY-MM-DD` entry at the top of
   `CHANGELOG.md`: a one-line summary, then `### Highlights` prose, then
   `### Added` / `### Changed` / `### Fixed` (and `### Security` when relevant),
   ending with a `---` separator. The one-line summary doubles as the release
   title. Commit and push.
2. **Merge** — merge `dev` into `main` and push.
3. **Tag** — on `main`:
   ```
   git tag vX.Y.Z && git push origin vX.Y.Z
   ```
4. **Release** — still on `main`:
   ```
   pwsh tools/publish-release.ps1
   ```
   This builds the release zip (Web + QaRunner at the root, ClaudeMock in
   `qa-mock/` — the layout `publish-stable.ps1` uses), verifies the MinVer
   version matches the tag, creates the GitHub release from the CHANGELOG entry,
   and uploads `KittyClaw-vX.Y.Z.zip`. Publishing the release is what triggers
   the in-app update banner: running instances poll `releases/latest`.
5. **Local stable instance** (optional) — republish `C:\KittyClaw-stable` with
   `pwsh tools/publish-stable.ps1`, or unzip the release asset there, then
   start `KittyClaw.Web.exe` (defaults to http://localhost:5230).

Afterwards, switch back to `dev` for further work.
