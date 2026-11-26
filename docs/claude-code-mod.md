# Claude Code tracking mod

## Purpose

The optional `plugins/kittyclaw` plugin adds a native Claude Code pane for choosing a ticket, preparing its work prompt, reviewing an assistant result, and publishing it as a comment. It runs inside Claude Code, independently of the provider subprocesses launched by KittyClaw.

## Components and entry points

- `.claude-plugin/marketplace.json` exposes the local plugin as `kittyclaw@kittyclaw`. Register the repository directory through `/plugin marketplace add`, then install with user scope to enable it across projects and future sessions. The GitHub registration path becomes usable after these files are pushed.
- `plugins/kittyclaw/.claude-plugin/plugin.json` identifies the plugin.
- `plugins/kittyclaw/hooks/hooks.json` loads `hooks/register.js` as a hooks module.
- `hooks/register.js` registers `/kittyclaw`, `/kittyclaw-work`, a native pane, tool-call tracking and turn-completion feedback. Session-scoped store keys retain the selected ticket and result draft.
- The pane calls the existing [REST API](rest-api.md) for project discovery, ticket reads and comment creation. It requires no new server endpoint or MCP configuration.
- `test/tracker.test.ts` uses Claude Code's native test kit for drawing and interaction tests; `test/api-smoke.mjs` verifies the journey against an isolated real API.

## Behavior and boundaries

Preparing work appends a ticket brief to the prompt for the user to review and send. Publication is an explicit pane action and writes a comment as `claude-code`. It never automatically changes ticket status or starts a provider run. Existing comment-driven workflows still apply. The workspace is displayed for the user to check; the mod does not switch directories or provision worktrees.

Pending publications retain a stable content marker. A retry reads the ticket and reconciles an existing identical comment before sending again. This handles a lost POST response but is not an atomic server-side idempotency guarantee. Server changes and ticket changes are blocked while a publication remains uncertain.

## Dependencies and usage

Requires Claude Code 2.1.332+ and a local HTTP KittyClaw instance. There are no third-party packages. See the [plugin README](../plugins/kittyclaw/README.md) for loading, the user journey, limitations and test commands.
