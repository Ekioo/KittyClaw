# KittyClaw-self tooling

KittyClaw-specific overrides used **only** when KittyClaw orchestrates its own development. Files here are **versioned in the repo** but deliberately **not embedded** into the third-party project template (which lives under `.agents/` and is shipped via `KittyClaw.Core.csproj` `<EmbeddedResource>` items). Third-party projects therefore never see anything in this folder.

## Contents

- `qa-tester/SKILL.md` — e2e variant of the qa-tester that drives `KittyClaw.QaRunner` (Playwright). Replaces the generic code-reading template when KittyClaw is dogfooding itself.
- `qa-tester/sample-scenarios/*.json` — example scenarios consumable by `KittyClaw.QaRunner` and useful as templates for new tickets.
- `setup.ps1` — copies the e2e `SKILL.md` and sample scenarios into your live KittyClaw workspace (`%APPDATA%\KittyClaw\projects\todo\.agents\qa-tester\`). Run once on the dev machine that hosts the KittyClaw self-development project.

## Usage

```powershell
pwsh tools/kittyclaw-self/setup.ps1
```

Re-run any time the e2e `SKILL.md` here changes if you want the live workspace to pick it up.
