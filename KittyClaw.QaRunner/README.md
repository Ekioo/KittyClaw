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

## Design principle: API for setup, Playwright for visuals

**Do all setup via `api` / shortcuts; reserve Playwright for the visual assertion only.**

If your test needs to create a ticket, assign it, and move it to a column before asserting something visual, do all of that with `createTicket` / `assignTicket` / `setStatus` actions — not with `click` / `fill` / `wait` chains. API calls are deterministic, instant, and don't break when a button is temporarily disabled or a dropdown hasn't rendered yet.

```
┌─────────────────────────────────────┬──────────────────────────────────────┐
│  Use api / shortcuts (setup)        │  Use Playwright (assertions)         │
├─────────────────────────────────────┼──────────────────────────────────────┤
│  Create a project                   │  Navigate to the resulting page      │
│  Create a ticket                    │  Take a screenshot                   │
│  Assign / change status             │  Assert CSS / text / visibility      │
│  Post a comment                     │  Assert JSON response (assertJson)   │
│  Any state precondition             │  Anything that needs a real render   │
└─────────────────────────────────────┴──────────────────────────────────────┘
```

Example: testing "ticket detail shows assignee badge":

```json
{
  "setup": [
    { "type": "createProject", "name": "qa-test" },
    { "type": "createTicket", "project": "qa-test", "title": "My ticket", "status": "Todo" },
    { "type": "assignTicket", "project": "qa-test", "assignedTo": "owner" }
  ],
  "actions": [
    { "type": "navigate", "url": "/projects/qa-test/tickets/{ticketId}" },
    { "type": "screenshot", "name": "ticket-detail" },
    { "type": "assertVisible", "selector": ".assignee-badge" }
  ],
  "verdict": { "passOn": "all-asserts-pass" }
}
```

## Variable substitution

All string fields in every action support `{varName}` placeholders. Variables are populated by:

- `createTicket` always stores the new ticket id under `{ticketId}`.
- Any action with an `extract` dictionary: `"extract": { "myVar": "some.dotted.path" }`.
- Previous `api` / shortcut results captured via `extract`.

Variables are resolved just before each action executes, so a later action can reference values captured by an earlier one.

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

These run before Playwright starts. All of them are also valid inside `actions`.

| `type`          | Fields                                | Effect                                                |
|-----------------|---------------------------------------|-------------------------------------------------------|
| `createProject` | `name`, optional `workspacePath`      | Creates a project on the test instance.               |
| `togglePause`   | `project`                             | Toggles `IsPaused` on a project.                      |
| `api`           | see below                             | Generic HTTP call.                                    |
| `createTicket`  | see below                             | Creates a ticket; stores id in `{ticketId}`.          |
| `assignTicket`  | see below                             | Assigns a ticket to a member.                         |
| `setStatus`     | see below                             | Moves a ticket to a column.                           |

### API action (`type: "api"`)

Generic HTTP call against the test instance.

```json
{
  "type": "api",
  "method": "POST",
  "path": "/api/projects/qa-test/tickets/{ticketId}/comments",
  "body": { "content": "Hello", "author": "qa-runner" },
  "headers": { "X-Custom": "value" },
  "extract": { "commentId": "id" }
}
```

| Field     | Required | Description                                                        |
|-----------|----------|--------------------------------------------------------------------|
| `method`  | no       | HTTP verb (default: `GET`).                                        |
| `path`    | yes      | Path relative to the test instance base URL. Variables resolved.  |
| `body`    | no       | JSON body. Variable placeholders inside string values are resolved.|
| `headers` | no       | Extra request headers. Values are variable-resolved.              |
| `extract` | no       | Map `varName → dotted.json.path` captured from the response body. |

### Convenience shortcuts

#### `createTicket`

```json
{
  "type": "createTicket",
  "project": "qa-test",
  "title": "My ticket",
  "status": "Todo",
  "priority": "Normal",
  "assignedTo": "programmer"
}
```

Always stores the created ticket id in `{ticketId}`. Add `extract` to capture additional fields.

#### `assignTicket`

```json
{
  "type": "assignTicket",
  "project": "qa-test",
  "value": "{ticketId}",
  "assignedTo": "owner"
}
```

`value` is the ticket id (defaults to `{ticketId}` if omitted).

#### `setStatus`

```json
{
  "type": "setStatus",
  "project": "qa-test",
  "value": "{ticketId}",
  "status": "InProgress"
}
```

`value` is the ticket id (defaults to `{ticketId}` if omitted).

### Browser actions

These require Playwright (run inside `actions`, after setup).

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
| `assertJson`   | `path`, `jsonPath`, `expected`           | GET `path`, extract `jsonPath` (dotted), assert equals `expected`.    |

#### `assertJson`

```json
{
  "type": "assertJson",
  "path": "/api/projects/qa-test/tickets/{ticketId}",
  "jsonPath": "assignedTo",
  "expected": "owner"
}
```

Issues a GET request and asserts a field in the JSON response. On mismatch, the failure message includes the full actual JSON.

### Verdict

- `passOn: "all-asserts-pass"` (default) — verdict is `PASS` only if every assertion passes.
- `passOn: "manual"` — verdict starts at `PASS`; the caller post-processes the result.

## First run

Downloads Chromium (~150 MB) into `%LOCALAPPDATA%\ms-playwright`. One-time per machine.

## Scope

This is internal tooling for the KittyClaw self-development workflow. The agent SKILLs that invoke it are not embedded into the third-party project template — each KittyClaw self-dev installation maintains its own qa-tester SKILL override locally (typically in `%APPDATA%\KittyClaw\projects\<slug>\.agents\qa-tester\`).
