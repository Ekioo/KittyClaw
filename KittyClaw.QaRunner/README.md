# KittyClaw.QaRunner

Headless Playwright .NET runner that:

1. Spawns an isolated `KittyClaw.Web` process on a free port with a throwaway data dir.
2. Drives Chromium against it using a JSON scenario (setup + actions + verdict).
3. Uploads any screenshots to a *target* KittyClaw API (typically the stable orchestrator that owns the ticket being validated).
4. Emits a `ScenarioResult` JSON on stdout.

Used by the KittyClaw self-development qa-tester to e2e-validate UI changes without polluting production state and without burning Anthropic tokens (the spawned instance uses the mock claude found by `ClaudeRunner`'s dynamic discovery).

## Invocation

```
KittyClaw.QaRunner --scenario qa-142.json --target-api http://localhost:5230 --ticket 142
```

Args:

- `--scenario <path>` — JSON file (see format below).
- `--target-api <url>` — API URL of the orchestrator to upload screenshots to.
- `--ticket <id>` — ticket number, only echoed back in logs / future result fields.
- `--web-exe <path>` — override the auto-discovered `KittyClaw.Web` executable.

Exit codes: `0` = PASS, `1` = FAIL, `2` = runtime error.

## Scenario format

```json
{
  "setup": [
    { "type": "createProject", "name": "qa-test", "workspacePath": "D:/Sources/Ekioo/Todo" },
    { "type": "togglePause", "project": "qa-test" }
  ],
  "actions": [
    { "type": "navigate", "url": "/" },
    { "type": "screenshot", "name": "home-paused", "description": "Home with paused project" },
    { "type": "assertCss", "selector": ".project-paused .project-pause-btn",
      "property": "color", "expected": "rgb(245, 158, 11)" }
  ],
  "verdict": { "passOn": "all-asserts-pass" }
}
```

### Setup actions (API-only, no browser)

| `type`          | Fields                                | Effect                                                |
|-----------------|---------------------------------------|-------------------------------------------------------|
| `createProject` | `name`, optional `workspacePath`      | Creates a project on the test instance.               |
| `togglePause`   | `project`                             | Toggles `IsPaused` on a project.                      |

### Browser actions

| `type`         | Fields                                   | Effect                                                                |
|----------------|------------------------------------------|-----------------------------------------------------------------------|
| `navigate`     | `url` (relative or absolute)             | Goes to the URL on the test instance.                                 |
| `click`        | `selector`                               | CSS selector click.                                                   |
| `fill`         | `selector`, `value`                      | Set input value.                                                      |
| `wait`         | `ms`                                     | Pause N ms.                                                           |
| `screenshot`   | `name`, optional `description`           | Full-page PNG. Uploaded post-run, URL placed in `result.screenshots`. |
| `assertCss`    | `selector`, `property`, `expected`       | Reads `getComputedStyle(...).getPropertyValue(prop)`.                 |
| `assertText`   | `selector`, `expected`                   | Reads `textContent`.                                                  |
| `assertVisible`| `selector`                               | Asserts element is visible.                                           |

### Verdict

- `passOn: "all-asserts-pass"` (default) — verdict is `PASS` only if every assertion passes.
- `passOn: "manual"` — verdict starts at `PASS`; the caller post-processes the result.

## First run

Downloads Chromium (~150 MB) into `%LOCALAPPDATA%\ms-playwright`. One-time per machine.

## Scope

This is internal tooling for the KittyClaw self-development workflow. The agent SKILLs that invoke it are not embedded into the third-party project template — each KittyClaw self-dev installation maintains its own qa-tester SKILL override locally (typically in `%APPDATA%\KittyClaw\projects\<slug>\.agents\qa-tester\`).
