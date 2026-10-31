# Kanban UI

## Purpose

The existing ticket automation queue also presents [persistent agent questions](ticket-questions.md)
on cards and below ticket descriptions, with owner answers and automatic processor resumption.
Blazor Server frontend for managing the board: visualize columns and tickets, edit them, browse activity, and watch agent runs live.

## Key components
- `KittyClaw.Web/` — Blazor Server app (`@rendermode InteractiveServer`).
- `KittyClaw.Web/wwwroot/css/` — application stylesheet, split into cohesive files loaded in order via `App.razor` (`base.css`, `shared.css`, `board.css`, `ticket-card.css`, `ticket-panel.css`, `agent-run.css`, `settings.css`, `chat.css`, `dashboard.css`, `home.css`, `settings-editor.css`).
- `KittyClaw.Web/wwwroot/js/` — JS interop helpers.
- `KittyClaw.Web/Services/BoardFilterState.cs` — per-circuit (scoped) service holding the board search filter text. Registered as `AddScoped` so each browser tab gets an independent instance; a Singleton registration would cause filter text typed in one tab to appear in all other open tabs.
- `KittyClaw.Web/Services/BoardSortState.cs` — singleton service that holds per-column sort settings (mode + direction) keyed by `slug\0column`. Exposes `ApplySort` to reorder a ticket list by Title, Priority, Assignee, CreatedAt, UpdatedAt, or cumulative agent token count with ascending/descending direction. Manual mode (default) leaves the original order unchanged.
- `KittyClaw.Web/Services/LatestRefreshGuard.cs` — latest-request-wins guard used by the project board and project directory so an older asynchronous reload cannot overwrite newer ticket or project state.
- `KittyClaw.Web/Components/Pages/UnifiedBoard.razor` — owns the common project directory and progressively loads ordered column summaries without opening ticket lists. `ProjectCards.razor` renders responsive project rows and quick links; `ProjectDirectorySearch` matches project names, slugs, pipelines and columns.
- `KittyClaw.Web/Markdown/CommentMarkdownPipeline.cs` — shared Markdig pipeline used to render ticket comments and activity entries; enables advanced extensions and treats soft line breaks as hard breaks so newlines typed in comments render visibly.
- `KittyClaw.Web/Components/App.razor` and `KittyClaw.Web/wwwroot/js/board.js` — critical board helpers are defined inline and the fingerprinted script is loaded before `blazor.web.js`, so an interactive circuit cannot start before URL replacement and scroll-preservation functions exist. The inline functions snapshot each `.column-body` `scrollTop` by index and replay it; `board.js` supplies the same public helpers for normal asset loading.
- `KittyClaw.Web/wwwroot/js/chat-drawer.js` `chatDrawerInstallPasteHandler` — listens for `paste` events on the chat textarea, extracts image clipboard items (JPEG, PNG, GIF, WebP; max 5 MB each; max 5 per turn), reads them as data URLs via `FileReader`, and bridges results to the Blazor component through `JSInvokable` callbacks (`OnImagePasted` / `OnImagePasteError`). The `File` objects are snapshotted synchronously inside the `paste` handler (browsers neuter `DataTransferItem`s once the handler returns), and the .NET bridge reference is refreshed on every install call because Blazor can recreate the component while retaining the DOM node. Plain-text pastes are not intercepted. The base64 data URLs cross the Blazor circuit, so `Program.cs` raises the SignalR `MaximumReceiveMessageSize` to 10 MB to accommodate the 5 MB per-image cap (~6.7 MB base64-encoded); the default 32 KB would close the circuit on any real screenshot.
- `KittyClaw.Web/Components/ChatDrawer.razor` — new-instruction drawer that stores the browser's last selected model in `localStorage` under a project-specific key. Existing conversations restore their own server-side model selection, including models no longer present in the current catalogue; legacy conversations without a stored binding recover the model from their last completed run. A new or reset conversation falls back to the available project-level browser preference, then to the default model.
- `KittyClaw.Web/Components/FolderPickerDialog.razor` — cross-platform in-app workspace browser used by project creation and project settings. It enumerates only directories, exposes filesystem roots and the user home, supports breadcrumbs, parent navigation, and direct absolute-path entry, and avoids native file dialogs that can open behind the browser window.
- `KittyClaw.Web/Components/WorkflowMigrationWizard.razor` — visual planner for project onboarding and scoped edits to existing pipelines. The legacy-board migration entry points have been removed.
- `KittyClaw.Web/Components/DecisionBriefPanel.razor` — renders the canonical [evidence decision brief](./evidence-decision-briefs.md) for tickets in owner-action columns, including findings, recovery guidance, provenance, contributing run IDs, and auditable accept/correct/stop actions.
- `KittyClaw.Web/Components/TicketPanel.razor` — displays incoming and outgoing ticket dependencies, links to related tickets, and exposes add/remove controls with validation errors returned inline.
- `KittyClaw.Web/Markdown/ChatMarkdownRenderer.cs` — static renderer for the chat drawer; wraps Markdig with a try/catch so a malformed message (e.g. deeply nested lists/quotes that trigger a Markdig nesting-depth error) falls back to HTML-encoded plain text with an inline warning instead of crashing the UI.
- `KittyClaw.Web/Services/AgentRunsState.cs` — singleton service that tracks which agent runs are currently active, keyed by project slug. `ProjectCards.razor` injects it and subscribes to `OnChange` (via `InvokeAsync(StateHasChanged)`) to reactively render a `.project-card-agent-badge` spinner on cards where `ActiveForProject(slug).Any()` is true.
- `KittyClaw.Web/Services/EscapeKeyStack.cs` + `EscapeKeyStackExtensions.cs` — scoped LIFO stack of Escape handlers. Components register a close callback via `Push` (or `PushWithFocus` to also save/restore focus through `wwwroot/js/escape-stack.js`) and dispose the returned token when their popup closes. `Components/EscapeKeyHost.razor` is mounted once in `MainLayout` and routes browser Escape keydowns to the topmost handler. Both `EscapeKeyHost.razor` and `MainLayout.razor` must declare `@rendermode InteractiveServer`; without it the JS interop call (`escapeStack.init`) is never executed and all Escape keydowns are silently dropped.
- Components consume the [storage](./storage.md) services directly via DI rather than self-calling the [REST API](./rest-api.md).

## Features
- Ticket details display the latest 30 timeline entries across comments, activities and JEV history. The load-more button expands the combined timeline by 30; refreshes preserve the expanded window. SQLite comment and activity queries are bounded to the visible window plus one entry. Other service callers retain complete history by default.
- Instruction chat, ticket run logs, and tile-creation chat share `ConversationDrawer`,
  `ConversationView`, `ConversationMessage`, `ConversationComposer`,
  `ConversationActivity`, and `ConversationTimestamp`. The view owns the common
  transcript surface; no entry point applies the legacy console layout to it.
  The common header retains run status, ticket, skill, session, concurrency group,
  model, duration and usage. The composer uses the application's navy surface tokens
  with compact attachment, model, inject and send/stop controls. Timestamp visibility
  is a browser preference shared across all three entry points, toggled from
  the conversation's context menu. Times sit inside message bubbles after the text,
  aligned right for outgoing messages and left for incoming messages. The composer
  keeps the native browser editing menu.
  On desktop, dragging the left border resizes the drawer between 360px and 95%
  of the viewport; the focused border also supports the left/right arrow keys.
  The width is shared by conversation drawers and saved in browser local storage
  when resizing ends, so reopening or reloading restores the preference.
  Double-clicking the border restores the default width and clears that preference.
  Phone conversations remain full-screen.
  Technical events are grouped chronologically behind activity disclosures; errors,
  user messages and agent replies remain visible. Raw payloads and diagnostic copying
  stay available. Each entry point retains its own session, streaming and submission
  lifecycle, including image paste, interactive questions, log retry, and tile
  configuration review. Tile creation also exposes live activity, model selection
  and cancellation without changing its configuration or script-validation flow.
- Ticket creation and detail panels no longer offer manual member assignment. Existing assignment data is no longer used by the ticket interface.
- Ticket cards display the first Markdown image from their description as a full-width cover above the card content, preserving its aspect ratio. Inline and reference-style images are supported; code samples and unsafe URL schemes are excluded.
- Phone layouts use `css/mobile.css`, loaded after component styles and scoped to widths up to 767px. Headers wrap actions into touch-sized rows with a full-width search field. Project and nested Kanbans show one full-width column at a time with mandatory horizontal snapping and a stop at each column; the whole page scrolls vertically, including headers and tickets, so wrapped controls cannot squeeze the ticket list into a small nested scroll area. Desktop tickets retain independent column scrolling. Ticket details stack in a full-screen panel, and chat/run drawers fill the screen. Dynamic viewport heights and safe-area padding accommodate mobile browser chrome. Desktop layouts and saved dashboard coordinates remain unchanged.
- The current column in the shared ticket panel is a native selector for the ticket's pipeline, usable by touch and keyboard. Changes use the existing routing validation, preserve undo support, refresh the project board, and display rejected moves inline.
- Ticket details have dedicated `TicketPanelMobileView` and `TicketPanelDesktopView` components. `TicketPanelResponsiveView` selects a single view through a `matchMedia` listener in `js/ticket-panel-viewport.js` and switches when crossing 767px. Both views receive shared content and command fragments from `TicketPanel`, keeping edits and service calls in one place. Mobile uses a full-width title, a sticky toolbar with secondary actions, compact status and label summaries, and a native Details disclosure for property and dependency editing. The whole mobile sheet scrolls as one surface; desktop keeps its directly accessible property bar and independently scrolling content. Mobile styles live in `css/ticket-panel-mobile.css`.
- Remaining phone compositions live in `css/mobile-views.css`. `MobileSectionNavigation` switches visible settings/workflow sections below 768px while keeping their forms mounted; desktop shows every section. Editable settings tables become labelled cards, cost filters expand on demand, and phone column headers expose the shared context menu as a bottom sheet. See the [mobile audit](./audits/2026-10-24-mobile-ux-audit.md) for the complete view inventory and verification boundaries.
- On phones the project header groups secondary controls behind an Actions button; its expanded state is announced to assistive technology. Desktop controls stay directly visible. Mobile filter sizing explicitly overrides the compact desktop header rules in `settings-editor.css`.
- `MobileColumnNavigation.razor` shows the current column name and position, a swipe hint, and previous/next buttons on mobile project and nested boards. `board.js` tracks native scrolling and column/viewport changes locally, disables arrows at either end, and detaches its observers when the component is disposed. The position is announced through a polite live region; the navigation is hidden for a single column and on desktop.
- Onboarding popup on first launch with Git, Claude Code, OpenAI Codex, Grok Build, Mistral Vibe, and Ollama detection.
- Project creation popup with an integrated Windows/macOS/Linux folder browser, template initialization, and guided workflow design.
- Multi-pipeline Kanban with drag-and-drop; processor routing lightly highlights allowed destinations and subdues forbidden columns while dragging.
- The project board applies only the latest overlapping ticket refresh, keeping externally moved tickets in their current column without disturbing the selected pipeline, filters, sorting, sub-kanban, or open ticket panel.
- The home routes `/`, `/board`, and `/projects` share one project directory. It shows project names, ordered columns with root-ticket counts and project status badges, with direct Kanban, dashboard and settings links. Desktop uses compact project rows; mobile stacks the project information above three touch-sized links. Search combines project names/slugs, pipeline names and column names; sorting retains the browser preference and places paused projects last. The directory renders its shell before loading column summaries sequentially and never loads ticket lists or ticket content. Column counts include zero-ticket columns, use stable column IDs, and match the root tickets visible on the project Kanban; child tickets belong to their nested boards. Multiple pipelines are displayed separately. Each column summary is a normal link carrying `pipeline` and `column` IDs in the board URL. After rendering, the board highlights and focuses the requested column and scrolls only its horizontal container; mobile column navigation follows the selected column. Invalid or removed column IDs leave the ordinary board available. The former multi-project swimlanes and display-mode toggle are removed.
- Ticket detail panel with comments and activity timeline. The initial ticket render includes its column selector and acceptance criteria; ancillary history, processor files and Git inspection load afterward without hiding the criteria. The responsive panel renders its desktop view before the viewport observer connects, including during prerendering, so a slow JavaScript module cannot leave an empty dark overlay. The observer switches to the mobile view when needed.
- Ticket detail reads use EF split queries for comments, activities and labels, keeping database result size additive as history grows instead of multiplying the three collections.
- The board, ticket and JEV panels transfer their initial data through Blazor's protected server component state. Interactive startup reuses the prerendered data instead of briefly replacing it with empty loading views. JEV panels load alongside the ticket, independently of Git inspection, reuse its initial ticket record, and reload on context changes or polling rather than every parent render. Repository proof checks run off the circuit so unrelated ticket controls remain responsive; decision services still validate freshness when applying decisions.
- Owner-action ticket panels expose a concise evidence decision brief and record the selected accept, correction, or stop decision in ticket history before moving the ticket.
- Live agent run drawer (SSE stream of provider output, steer + stop controls). The context section shows the trigger signal, triggering owner comment, consumed ticket version, and machine-readable refusal reason when a stale or unreadable context blocks Success. Failed steer, stop, and retry requests leave the drawer open and display a concise French error so the action can be attempted again.
- Animated spinner badge on project cards (`ProjectCards.razor`) when one or more agent runs are active for that project (sourced from `AgentRunsState`).
- New-instruction chat drawer to send an ad-hoc prompt to an agent, with image paste support (paste screenshots or images directly into the textarea; preparation status, errors, and thumbnails render in a `chat-paste-tray` directly above the prompt composer before send; a 10-second watchdog replaces a stalled preparation with a visible error; up to 5 images per turn). Each conversation keeps its selected model across turns and drawer reopenings. New conversations use the last available model selected for the project in the same browser; resetting a conversation clears its server-side model binding. When the agent invokes `AskUserQuestion`, the chat drawer renders an interactive prompt widget — either multiple-choice buttons or a free-text textarea — and the user's answer is sent back via the `/steer` endpoint. Answered widgets are locked in place so history remains readable.
- Markdown rendering with `@mention`, `#id`, and `#{slug}:{id}` cross-project ticket references.
- Advanced search syntax: `#42`, `@owner`, `>date`, `priority:critical`, `label:bug`, `by:owner`.
- Sub-tickets with parent/child relationships and progress tracking.
- Ticket cards show a compact lock count while blockers remain unresolved. The ticket panel lists both dependency directions with ticket id, title, and status, and supports adding or removing blockers.
- Ticket scheduling from the ticket panel, with a due-date badge and chronological ordering in the Scheduled column. See [ticket scheduling](./ticket-scheduling.md).
- Contextual column management from the right-click menu: configure identity, role, guidance, processor, actions, schedules, and routing; insert, duplicate, sort, or mark read without leaving the board. Image upload, label management, and member management remain available in their dedicated interfaces.
- Column scroll preservation: opening or closing the ticket detail panel calls `saveColumnScrollPositions` and restores positions in `OnAfterRenderAsync` via `restoreColumnScrollPositions` once Blazor has re-rendered. `Board.razor` treats missing optional JavaScript helpers as a recoverable condition, preserving tab interactivity after an update; URL replacement falls back to Blazor navigation when `boardReplaceUrl` is unavailable.
- Right-click context menu on column headers to sort tickets bidirectionally (Title, Priority, Assignee, Created date, Modified date, Token count) or reset to manual order. Sort state is held in `BoardSortState` and survives navigation within the same circuit.
- Right-click context menu on ticket cards to open the ticket or move it directly to any column in any pipeline. The current destination is disabled, moves participate in the board's undo history, and the board refreshes after a move.
- Escape key closes the topmost open popup, drawer, or menu (board ticket panel, run drawer, chat drawer, project and processor dialogs, board context menus), with previously-focused element restoration. When a ticket panel is opened via URL param (`?ticketId=N`) rather than a click, `Board.razor` registers the ESC handler in `OnAfterRenderAsync` (guarded by `_selectedTicket is not null && _escTicketPanel is null`). On `TicketId` parameter change, `OnParametersSetAsync` disposes and nulls the stale token so the post-render guard re-registers it for the new ticket. The fullscreen description/comment editor (`OpenFullscreen`) also registers an ESC handler via `EscapeStack.PushWithFocus`; it records `_fullscreenOriginalText` on open, and pressing ESC triggers a dirty-check — if `_fullscreenText != _fullscreenOriginalText`, a native Blazor modal (`_showDiscardConfirm`) is shown asking the user to discard or keep editing (replaces the old `JS.InvokeAsync<bool>("confirm",…)` call). The ESC callback is synchronous (non-async); `InvokeAsync` is used for thread marshalling. If the user cancels the discard, `CancelDiscard` re-registers the ESC handler so subsequent ESC presses still trigger the dirty-check. `_escFullscreenEditor` is disposed in both `CancelFullscreen` and `SaveFullscreen` so the handler is never left dangling.
- When a deployment invalidates an already-open Blazor circuit, `ReconnectModal.razor.js` observes the fatal circuit banner, verifies `/api/engine/health`, and reloads the stale page once. A one-minute session guard prevents reload loops for genuine component exceptions; those retain a localized, dark-theme recovery banner with an explicit reload action.

## Entry points
- `http://localhost:5230/` (default port served by `dotnet watch`).

## External dependencies
- [Storage](./storage.md) — domain services for tickets, columns, members, labels.
- [Agent dispatch](./agent-dispatch.md) — backs the run drawer and the new-instruction chat.

### Ticket improvement

The shared ticket panel and right-click menus on the project board offer **Improve with AI**. `TicketImprovementService` requests a structured proposal through the existing provider runner. The editable preview keeps description and acceptance criteria separate and flags unknown information as questions. Saving replaces both atomically, rejects concurrent content changes, preserves assessments only for identical criteria, and records an activity entry. Closing the dialog cancels the analysis.

### Chat model effort

The chat composer displays an effort selector next to its model picker. `ModelEffort`
lists supported values per known model; unknown models and providers without a
supported effort transport display a disabled control. Supported models start with an explicit `medium` selection, which is always sent to
the CLI instead of inheriting a hidden provider configuration. Changing models resets
effort to this explicit initial value. Legacy conversations without an effort also
resolve to `medium` on load and dispatch.

`ChatStartRequest.Effort` is validated against the effective conversation model before
any chat writes or dispatch. The selection is stored per conversation in
`SessionRegistry` and returned by `/chat/model` when reopening the drawer. Changing
effort applies to the next turn without clearing conversation history. Claude receives
`--effort`; Codex receives a `model_reasoning_effort` config override, including on resume.

## Conversational ticket creation

`BoardCreateTicketDialog` opens in the same `ConversationDrawer`, `ConversationView`,
`ConversationMessage` and `ConversationComposer` used by New instruction. It opens
in AI assisted mode, alongside the preserved Manual
form. `TicketIntakeService` runs one bounded provider invocation per message and passes
conversation history as structured context. Inspired by
[grill-me](https://www.skills.sh/mattpocock/skills/grill-me), it asks one focused
question at a time about outcomes, scope, constraints and tradeoffs. Before asking technical questions, the agent inspects repository guidance and
relevant source using file tools and read-only commands in the resolved project
workspace. The prompt limits tools to inspection (this is an instruction, not an
OS-enforced read-only sandbox). Unknown facts remain questions or explicit
assumptions. Each response allows up to 20 provider turns and five minutes for
inspection; only the final assistant message is parsed as the structured reply.
The agent returns a validated JSON reply, optionally containing an actionable
English title and description. The UI owns creation through `TicketService`,
retaining the selected pipeline, parent, labels and priority.

Automatic creation is enabled by default and can be disabled before sending for
manual review. Create now requests a draft immediately from the available answers;
unanswered decisions must be recorded rather than invented. Switching tabs keeps
both drafts mounted. Closing cancels the current provider turn. Browser session storage
preserves conversation history, the unsent message, assisted/manual mode, creation
preference and all prepared ticket fields per project, pipeline and parent ticket.
Reopening New Ticket restores this draft, including after a page reload in the same
tab. Only successful ticket creation clears it; failed creation retains it.
Provider failures leave the conversation
available, and creation failures retain the generated draft for retry or editing.
