# KittyClaw Claude Code mod

A native tracking pane for the **choose a ticket → work on it → publish a result** journey. Requires Claude Code **2.1.332+** and a running local KittyClaw instance. No Node dependencies or build step are needed by the mod.

## Install from Claude Code

In an existing Claude Code session, register this local checkout and install the plugin with user scope:

```text
/plugin marketplace add D:/Sources/Ekioo/KittyClaw
/plugin install kittyclaw@kittyclaw --scope user
/reload-plugins
```

If Claude opens the plugin details instead of installing immediately, select **Install for you (user scope)**. User scope enables the plugin across projects and future sessions. Then run `/kittyclaw` to open the pane. Keep this checkout at the registered location.

After the marketplace and plugin files have been committed and pushed to GitHub, another machine can register the published repository instead:

```text
/plugin marketplace add Ekioo/KittyClaw
/plugin install kittyclaw@kittyclaw --scope user
/reload-plugins
```

For updates, run `/plugin marketplace update kittyclaw`, then `/plugin update kittyclaw@kittyclaw`, and `/reload-plugins` in an open session. If Desktop still shows an old version, fully quit and reopen it. The tested CLI 2.1.332 can read a directory marketplace directly, while Desktop can keep the installed cached copy. After changing the plugin version in this checkout, run `claude plugin update kittyclaw@kittyclaw --scope user` to refresh that installed copy too.

## Load for one development session

From the repository root:

```powershell
claude --plugin-dir ./plugins/kittyclaw
```

In Claude Code, run `/kittyclaw`. The default server is `http://localhost:5230`; change the address and use the field's **Connect** action (or press Enter) to use another local instance. Once connected, connection settings collapse; use **Connection** to open them again. This first version supports local HTTP only. It uses the REST API directly, so enabling KittyClaw's MCP server is unnecessary.

1. Select a project and find a ticket by title, number or status. The pane shows its description, status, acceptance criteria and the current session workspace.
2. Check that Claude Code is open in the intended repository. **Prepare work prompt** appends the ticket brief to your prompt, preserving any existing draft. Review it and press Send. `/kittyclaw-work` also prepares the selected ticket.
3. Work normally. The pane counts tool calls while work is active. **Refresh** reloads the ticket from KittyClaw.
4. After Claude finishes, click **Use latest response**, or write a result yourself. Review and edit the result field, then click **Publish to ticket**. It posts as `claude-code` and keeps the ticket in its current column.

Selection and result drafts are stored per Claude session, and restored when that session resumes. Nothing is posted automatically. The mod does not change the session's directory, create a worktree, claim a ticket, move columns, or dispatch another agent. Publishing a comment may trigger workflows configured by the project.

After an uncertain network write, retry publication: the saved comment marker is reconciled with existing ticket comments before another POST. This prevents duplicates when a response was lost, but is not server-side atomic idempotency. A truncated comment history blocks retry rather than risking a duplicate.

## Verification

```powershell
claude plugin validate ./plugins/kittyclaw
claude plugin validate .
claude plugin test ./plugins/kittyclaw
```

Tests exercise the native mod runtime, render the pane for terminal/Desktop/VS Code, prepare the prompt, publish only on click, and reconcile a lost write response without another POST. They call no models.

For a real API smoke test, start an **isolated** KittyClaw instance with a fresh data directory and mocked provider executables, then:

```powershell
$env:KITTYCLAW_TEST_URL = 'http://localhost:5242'
node plugins/kittyclaw/test/api-smoke.mjs
```

This creates a synthetic project and ticket in that test instance. Ports 5230 and 5231 are refused. Do not point it at a real project instance. Visual painting in a real interactive Claude Code session should still be checked; native drawing tests validate element trees, not pixels.

Implementation follows the official [mods API](https://code.claude.com/docs/en/plugins/mods/api) and [native pane API](https://code.claude.com/docs/en/plugins/mods/interface).
