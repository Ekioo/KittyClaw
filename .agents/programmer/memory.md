# Memory — programmer

## Performance (last evaluated: 2026-04-19)
| Metric                    | Value | Trend |
|---------------------------|-------|-------|
| First-pass success rate   | 50%   | ↓     |
| Feedback compliance       | 1.0   | →     |
| Delivery quality          | 63%   | ↓     |
| Block rate                | 0%    | →     |
| Tickets evaluated         | 4     | —     |

## Lessons learned

- `curl` with special characters (apostrophes, accents, backticks) in inline JSON causes UTF-8 encoding errors on Windows — always use a temp file [+4]
- Polish the delivery comment: accents, readability, one sentence per deliverable — the reviewer relies on it to validate without re-reading the code [+2]
- Mention intermediate verifications (e.g. GET before PUT) in the delivery comment to document the full reasoning [+1]
- When the owner asks a question in a ticket comment (e.g. "couldn't this be centralized?"), it is a refactoring instruction — implement it directly [+1]

## Success patterns

- Direct complete delivery with no Todo return and no block [+2]
- When owner feedback says "not visible in the UI", verify all four entry points: palette (NodePalette.razor), editor (ActionEditor.razor), DnD factory (BuildAction in Automations.razor), localization — all four must be updated [+2]
- [1] Missing UI-node checklist on first delivery (#67) — apply systematically before Review: NodePalette + ActionEditor + BuildAction DnD + EN+FR localization

## Anti-patterns

- When adding a new drawer/panel component to Board.razor, always wrap it in `ErrorBoundary` — ClaudeChatDrawer has one, AgentRunDrawer was missing one, causing the full Blazor crash [+1]
- Razor components with nested `@foreach` loops must use distinct variable names — CS0136 prevents compilation [+1]

## Owner preferences

- Prefer centralization (DRY) over repetition across files [+1]

## Metrics

- For an "extract method" refactor with shared mutable state between switch cases, group that state in an ActionState object passed as a parameter — cleaner than ref params [+1]
- When adding markdown rendering to a console/log component, always override `white-space: pre-wrap` for the markdown container — otherwise block elements (p, ul, h*) won't render correctly [+1]
- Reuse the existing `_mdPipeline` static field pattern with `UseAdvancedExtensions()` and `MarkupString` — consistent with Board.razor and ClaudeChatDrawer.razor [+1]
- When fixing event ordering in a streaming pipeline, push the parent event first then derived events — preserves logical chronology in the log view [+1]
- When HTML-encoding regex-highlighted output, run the regex on the raw text first, then `HtmlEncode` each token — never encode before tokenizing [+1]
- Use CSS custom property `--sc` pattern (like `--lc` for labels) for per-chip color injection on dynamic lists [+1]
- When `FlattenJson` returns raw compact JSON as message body, the markdown renderer shows it unformatted — check all rendering branches (user/assistant/system) for JSON content and route to `HighlightJson` [+1]
- New event kinds in AgentRunDrawer (e.g. result, future types) need their own `else if` branch with a collapsible `<details>` block — the `else` fallback renders as plain text [+1]
- `JsonSerializer.Serialize(JsonDocument, options)` does NOT reliably honor `WriteIndented` — always use `doc.RootElement` instead [+1]
- CSS `word-break: break-all` destroys JSON indentation visual structure — use `overflow-wrap: break-word` for pre-wrapped code blocks [+1]
- tickets_completed [15]
- tickets_returned_todo [0]
- builds_broken [0]
- tickets_blocked [0]
