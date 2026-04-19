# Memory — programmer

## Performance (last evaluated: 2026-04-19)
| Metric                    | Value | Trend |
|---------------------------|-------|-------|
| First-pass success rate   | 80%   | ↑     |
| Feedback compliance       | 1.0   | →     |
| Delivery quality          | 72%   | ↑     |
| Block rate                | 0%    | →     |
| Tickets evaluated         | 5     | —     |

## Lessons learned

- `curl` with special characters (apostrophes, accents) in inline JSON causes UTF-8 encoding errors on Windows — always use a heredoc or temp file [+3]
- Polish the delivery comment: accents, readability, one sentence per deliverable — the reviewer relies on it to validate without re-reading the code [+2]
- Mention intermediate verifications (e.g. GET before PUT) in the delivery comment to document the full reasoning [+1]
- When the owner asks a question in a ticket comment (e.g. "couldn't this be centralized?"), it is a refactoring instruction — implement it directly [+1]

## Success patterns

- Direct complete delivery with no Todo return and no block [+2]
- When owner feedback says "not visible in the UI", verify all four entry points: palette (NodePalette.razor), editor (ActionEditor.razor), DnD factory (BuildAction in Automations.razor), localization — all four must be updated [+2]
- [1] Missing UI-node checklist on first delivery (#67) — apply systematically before Review: NodePalette + ActionEditor + BuildAction DnD + EN+FR localization

## Anti-patterns

## Owner preferences

- Prefer centralization (DRY) over repetition across files [+1]

## Metrics

- For an "extract method" refactor with shared mutable state between switch cases, group that state in an ActionState object passed as a parameter — cleaner than ref params [+1]
- When adding markdown rendering to a console/log component, always override `white-space: pre-wrap` for the markdown container — otherwise block elements (p, ul, h*) won't render correctly [+1]
- Reuse the existing `_mdPipeline` static field pattern with `UseAdvancedExtensions()` and `MarkupString` — consistent with Board.razor and ClaudeChatDrawer.razor [+1]
- tickets_completed [7]
- tickets_returned_todo [0]
- builds_broken [0]
- tickets_blocked [0]
