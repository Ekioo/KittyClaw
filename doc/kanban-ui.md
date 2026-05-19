# Kanban UI

## Purpose
Blazor Server frontend for managing the board: visualize columns and tickets, edit them, browse activity, and watch agent runs live.

## Key components
- `KittyClaw.Web/` — Blazor Server app (`@rendermode InteractiveServer`).
- `KittyClaw.Web/wwwroot/app.css` — single global stylesheet.
- `KittyClaw.Web/wwwroot/js/` — JS interop helpers.
- `KittyClaw.Web/Services/BoardFilterState.cs` — per-circuit (scoped) service holding the board search filter text. Registered as `AddScoped` so each browser tab gets an independent instance; a Singleton registration would cause filter text typed in one tab to appear in all other open tabs.
- `KittyClaw.Web/Services/BoardSortState.cs` — singleton service that holds per-column sort settings (mode + direction) keyed by `slug\0column`. Exposes `ApplySort` to reorder a ticket list by Title, Priority, Assignee, CreatedAt, or DueDate with ascending/descending direction. Manual mode (default) leaves the original order unchanged.
- `KittyClaw.Web/Markdown/CommentMarkdownPipeline.cs` — shared Markdig pipeline used to render ticket comments and activity entries; enables advanced extensions and treats soft line breaks as hard breaks so newlines typed in comments render visibly.
- `KittyClaw.Web/wwwroot/js/chat-drawer.js` `chatDrawerInstallPasteHandler` — listens for `paste` events on the chat textarea, extracts image clipboard items (JPEG, PNG, GIF, WebP; max 5 MB each; max 5 per turn), reads them as data URLs via `FileReader`, and bridges results to the Blazor component through `JSInvokable` callbacks (`OnImagePasted` / `OnImagePasteError`). Plain-text pastes are not intercepted.
- `KittyClaw.Web/Markdown/ChatMarkdownRenderer.cs` — static renderer for the chat drawer; wraps Markdig with a try/catch so a malformed message (e.g. deeply nested lists/quotes that trigger a Markdig nesting-depth error) falls back to HTML-encoded plain text with an inline warning instead of crashing the UI.
- `KittyClaw.Web/Services/EscapeKeyStack.cs` + `EscapeKeyStackExtensions.cs` — scoped LIFO stack of Escape handlers. Components register a close callback via `Push` (or `PushWithFocus` to also save/restore focus through `wwwroot/js/escape-stack.js`) and dispose the returned token when their popup closes. `Components/EscapeKeyHost.razor` is mounted once in `MainLayout` and routes browser Escape keydowns to the topmost handler.
- Components consume the [storage](./storage.md) services directly via DI rather than self-calling the [REST API](./rest-api.md).

## Features
- Onboarding popup on first launch with Claude Code + Git detection.
- Project creation popup with workspace selection + one-click agent template initialization.
- Kanban board with drag-and-drop.
- Ticket detail panel with comments and activity timeline.
- Live agent run drawer (SSE stream of Claude Code output, steer + stop controls).
- New-instruction chat drawer to send an ad-hoc prompt to an agent, with image paste support (paste screenshots or images directly into the textarea; thumbnails shown before send; up to 5 images per turn).
- Automations page: list, enable/disable, edit (triggers / conditions / actions), reload from disk, re-initialize agent template.
- Markdown rendering with `@mention`, `#id`, and `#{slug}:{id}` cross-project ticket references.
- Advanced search syntax: `#42`, `@owner`, `>date`, `priority:critical`, `label:bug`, `by:owner`.
- Sub-tickets with parent/child relationships and progress tracking.
- Column management (create, reorder, customize colors), label and member management, image upload.
- Right-click context menu on column headers to sort tickets bidirectionally (Title, Priority, Assignee, Created date, Due date) or reset to manual order. Sort state is held in `BoardSortState` and survives navigation within the same circuit.
- Escape key closes the topmost open popup, drawer, or menu (board ticket panel, run drawer, chat drawer, project/automation dialogs, board context menus, label manager, member manager), with previously-focused element restoration.

## Entry points
- `http://localhost:5230/` (default port served by `dotnet watch`).

## External dependencies
- [Storage](./storage.md) — domain services for tickets, columns, members, labels.
- [Agent dispatch](./agent-dispatch.md) — backs the run drawer and the new-instruction chat.
- [Automation engine](./automation-engine.md) — backs the Automations page.
