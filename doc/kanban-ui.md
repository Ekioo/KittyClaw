# Kanban UI

## Purpose
Blazor Server frontend for managing the board: visualize columns and tickets, edit them, browse activity, and watch agent runs live.

## Key components
- `KittyClaw.Web/` — Blazor Server app (`@rendermode InteractiveServer`).
- `KittyClaw.Web/wwwroot/app.css` — single global stylesheet.
- `KittyClaw.Web/wwwroot/js/` — JS interop helpers.
- `KittyClaw.Web/Services/BoardFilterState.cs` — per-circuit (scoped) service holding the board search filter text. Registered as `AddScoped` so each browser tab gets an independent instance; a Singleton registration would cause filter text typed in one tab to appear in all other open tabs.
- `KittyClaw.Web/Services/EscapeKeyStack.cs` + `EscapeKeyStackExtensions.cs` — scoped LIFO stack of Escape handlers. Components register a close callback via `Push` (or `PushWithFocus` to also save/restore focus through `wwwroot/js/escape-stack.js`) and dispose the returned token when their popup closes. `Components/EscapeKeyHost.razor` is mounted once in `MainLayout` and routes browser Escape keydowns to the topmost handler.
- Components consume the [storage](./storage.md) services directly via DI rather than self-calling the [REST API](./rest-api.md).

## Features
- Onboarding popup on first launch with Claude Code + Git detection.
- Project creation popup with workspace selection + one-click agent template initialization.
- Kanban board with drag-and-drop.
- Ticket detail panel with comments and activity timeline.
- Live agent run drawer (SSE stream of Claude Code output, steer + stop controls).
- New-instruction chat drawer to send an ad-hoc prompt to an agent.
- Automations page: list, enable/disable, edit (triggers / conditions / actions), reload from disk, re-initialize agent template.
- Markdown rendering with `@mention`, `#id`, and `#{slug}:{id}` cross-project ticket references.
- Advanced search syntax: `#42`, `@owner`, `>date`, `priority:critical`, `label:bug`, `by:owner`.
- Sub-tickets with parent/child relationships and progress tracking.
- Column management (create, reorder, customize colors), label and member management, image upload.
- Escape key closes the topmost open popup, drawer, or menu (board ticket panel, run drawer, chat drawer, project/automation dialogs, board context menus), with previously-focused element restoration.

## Entry points
- `http://localhost:5230/` (default port served by `dotnet watch`).

## External dependencies
- [Storage](./storage.md) — domain services for tickets, columns, members, labels.
- [Agent dispatch](./agent-dispatch.md) — backs the run drawer and the new-instruction chat.
- [Automation engine](./automation-engine.md) — backs the Automations page.
